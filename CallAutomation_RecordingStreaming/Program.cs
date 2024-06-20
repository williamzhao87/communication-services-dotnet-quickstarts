using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using RecordingStreaming.Interfaces;
using RecordingStreaming.Models;
using RecordingStreaming.Services;
using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var client = new CallAutomationClient(new Uri("https://pma.plat.skype.com"), builder.Configuration["ACSConnectionString"]);
var callbackUriBase = builder.Configuration["CallbackUri"];
var websocketUri = builder.Configuration["WebsocketUri"];

builder.Services.AddSingleton<IEventsService, EventsService>();
builder.Services.AddSingleton<IStorageService, BlobStorageService>();
builder.Services.AddSingleton<ITelemetryService, TelemetryService>();
builder.Services.AddHttpClient();
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddControllers();

var app = builder.Build();
app.Logger.LogInformation($"CALLBACK URI: {callbackUriBase}");

var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
};

app.UseWebSockets(webSocketOptions);

#region Handle Incoming Call

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents, IConfiguration configuration) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        if (eventData is AcsIncomingCallEventData incomingCallEventData)
        {
            var callerId = incomingCallEventData.FromCommunicationIdentifier.RawId;
            var target = incomingCallEventData.ToCommunicationIdentifier.RawId;
            var serverCallId = incomingCallEventData.ServerCallId;
            var recordingId = Guid.NewGuid().ToString();
            var callbackUri = new Uri(callbackUriBase + $"/api/calls/{recordingId}?callerId={callerId}");
            var pauseOnStart = builder.Configuration["PauseOnStart"] == "true";

            if (configuration["BotMri"].Split(",").Contains(target))
            {
                var activeCall = new ActiveCall { RecordingId = recordingId, CallerId = callerId };
                if (!pauseOnStart)
                {
                    activeCall.StartRecordingWithAnswerTimer = new Stopwatch();
                    activeCall.StartRecordingWithAnswerTimer.Start();
                    activeCall.StartRecordingEventTimer = new Stopwatch();
                    activeCall.StartRecordingEventTimer.Start();
                }
                activeCall.CallConnectedTimer = new Stopwatch();
                activeCall.CallConnectedTimer.Start();

                CallContextService.SetActiveCall(serverCallId, activeCall);

                var call = await client.AnswerCallAsync(new AnswerCallOptions(incomingCallEventData.IncomingCallContext, callbackUri)
                {
                    RecordingOptions = new RecordingOptions
                    {
                        RecordingServiceUrl = new Uri(websocketUri),
                        ExternalStorage = new BlobStorage(new Uri("https://williamzhaostorage.blob.core.windows.net/recording-stream")),
                        RecordingChannel = RecordingChannel.Mixed,
                        PauseOnStart = pauseOnStart
                    }
                });

                // Cannot use this because MediaStreamingSubscription is currently null
                CallContextService.MediaSubscriptionIdsToServerCallId[call.Value.CallConnectionProperties.MediaSubscriptionId] = serverCallId;
                CallContextService.CallConnectionIdsToServerCallId[call.Value.CallConnection.CallConnectionId] = serverCallId;
                CallContextService.CallIdsToServerCallId[call.Value.CallConnectionProperties.CorrelationId] = serverCallId;
                CallContextService.SetActiveCall(serverCallId, new ActiveCall
                {
                    CallConnection = call.Value.CallConnection,
                    CallConnectionProperties = call.Value.CallConnectionProperties,
                    CallId = call.Value.CallConnectionProperties.CorrelationId,
                    SubscriptionId = call.Value.CallConnectionProperties.MediaSubscriptionId
                });

                app.Logger.LogInformation($"CallConnection: {JsonSerializer.Serialize(call.Value)}");
            }
        }
    }
    return Results.Ok();
});

#endregion

#region Handle Call Automation Events

app.MapPost("/api/calls/{recordingId}", async ([FromBody] CloudEvent[] cloudEvents, [FromRoute] string recordingId,
    [FromQuery] string callerId, ITelemetryService telemetryService, IConfiguration configuration) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        app.Logger.LogInformation($"Received callback event: {@event.GetType()}");
        var pauseOnStart = builder.Configuration["PauseOnStart"] == "true";

        if (@event is CallConnected callConnected)
        {
            var serverCallId = CallContextService.CallConnectionIdsToServerCallId[callConnected.CallConnectionId];
            var activeCall = CallContextService.GetActiveCall(serverCallId);
            var elapsedTime = activeCall.CallConnectedTimer.ElapsedMilliseconds;
            activeCall?.CallConnectedTimer?.Stop();
            app.Logger.LogInformation($"*******CALL CONNECTED elapsed milliseconds: {elapsedTime} *************");
            await telemetryService.LogLatenciesAsync(new[]
            {
                new LatencyRecord
                {
                    action_type = "CallConnected",
                    env = "Prod",
                    region = "USWest",
                    value = elapsedTime,
                    call_id = activeCall.CallId,
                    scenario = pauseOnStart ? "RecordingMidCall" : "RecordingWithAnswer"
                }
            });
        }
        else if (@event is RecordingStateChanged recordingStateChanged)
        {
            var serverCallId = CallContextService.CallConnectionIdsToServerCallId[recordingStateChanged.CallConnectionId];
            var activeCall = CallContextService.GetActiveCall(serverCallId);
            if (recordingStateChanged.State == RecordingState.Active &&
                (activeCall.StartRecordingEventTimer?.IsRunning ?? false))
            {
                var elapsedTime = activeCall.StartRecordingEventTimer.ElapsedMilliseconds;
                activeCall.StartRecordingEventTimer.Stop();
                app.Logger.LogInformation(
                    $"*******START RECORDING EVENT RECEIVED elapsed milliseconds: {elapsedTime} *************");
                await telemetryService.LogLatenciesAsync(new[]
                {
                    new LatencyRecord
                    {
                        action_type = pauseOnStart ? "StartRecordingEvent" : "StartRecordingWithAnswerEvent",
                        env = "Prod",
                        region = "USWest",
                        value = elapsedTime,
                        call_id = activeCall.CallId,
                        scenario = pauseOnStart ? "RecordingMidCall" : "RecordingWithAnswer"
                    }
                });
            }
        }
        else if (@event is MediaStreamingFailed mediaStreamingFailed)
        {
            app.Logger.LogWarning($"{JsonSerializer.Serialize(mediaStreamingFailed)}");
        }
        else if (@event is MediaStreamingStopped mediaStreamingStopped)
        {
            var serverCallId = CallContextService.CallConnectionIdsToServerCallId[mediaStreamingStopped.CallConnectionId];
            var activeCall = CallContextService.GetActiveCall(serverCallId);
            if (activeCall.StopRecordingEventTimer?.IsRunning ?? false)
            {
                var elapsedTime = activeCall.StopRecordingEventTimer.ElapsedMilliseconds;
                activeCall.StopRecordingEventTimer.Stop();
                app.Logger.LogInformation(
                    $"*******STOP RECORDING EVENT RECEIVED elapsed milliseconds: {elapsedTime} *************");
                await telemetryService.LogLatenciesAsync(new[]
                {
                    new LatencyRecord
                    {
                        action_type = "StopRecordingEvent",
                        env = "Prod",
                        region = "USWest",
                        value = elapsedTime,
                        call_id = activeCall.CallId,
                        scenario = pauseOnStart ? "RecordingMidCall" : "RecordingWithAnswer"
                    }
                });
            }
        }
        else if (@event is CallDisconnected callDisconnected)
        {
            CallContextService.RemoveActiveCall(callDisconnected.ServerCallId);
        }
    }

    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

#endregion

#region Start Recording

app.MapPost("/api/calls/{callId}:startRecording", async ([FromRoute] string callId) =>
{
    var serverCallId = CallContextService.CallIdsToServerCallId[callId];
    var activeCall = CallContextService.GetActiveCall(serverCallId);
    activeCall.StartRecordingTimer = new Stopwatch();
    activeCall.StartRecordingTimer.Start();
    activeCall.StartRecordingEventTimer = new Stopwatch();
    activeCall.StartRecordingEventTimer.Start();
    CallContextService.SetActiveCall(serverCallId, activeCall);

    await client.GetCallConnection(activeCall.CallConnection.CallConnectionId).GetCallMedia()
        .StartRecordingStreamingAsync();

    return Results.Ok();
});

#endregion

#region Pause Recording

app.MapPost("/api/calls/{callId}:stopRecording", async ([FromRoute] string callId) =>
{
    var serverCallId = CallContextService.CallIdsToServerCallId[callId];
    var activeCall = CallContextService.GetActiveCall(serverCallId);
    activeCall.StopRecordingTimer = new Stopwatch();
    activeCall.StopRecordingTimer.Start();
    activeCall.StopRecordingEventTimer = new Stopwatch();
    activeCall.StopRecordingEventTimer.Start();
    CallContextService.SetActiveCall(serverCallId, activeCall);

    await client.GetCallConnection(activeCall.CallConnection.CallConnectionId).GetCallMedia()
        .StopRecordingStreamingAsync();

    return Results.Ok();
});

#endregion

#region Download Recording

app.MapPost("/api/recordingDone", async ([FromBody] EventGridEvent[] eventGridEvents, IStorageService storageService) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        // Handle recording status updated event - download recording to local folder
        if (eventData is AcsRecordingFileStatusUpdatedEventData acsRecordingFileStatusUpdatedEventData)
        {
            var recordingDownloadUri = acsRecordingFileStatusUpdatedEventData.RecordingStorageInfo.RecordingChunks[0].ContentLocation;
            // Download media from recording location
            await storageService.DownloadTo(recordingDownloadUri, "recording.wav");
        }
    }
    return Results.Ok();
});

#endregion

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "audio")),
    RequestPath = "/audio"
});

app.MapControllers();

app.Run();