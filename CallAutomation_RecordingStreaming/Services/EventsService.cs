using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using RecordingStreaming.Interfaces;
using System.Text.Json;

namespace RecordingStreaming.Services
{
    public class EventsService : IEventsService
    {
        private readonly HttpClient _httpClient;

        public EventsService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.BaseAddress = new Uri($"{configuration["CallbackUri"]}");
        }

        public async Task SendRecordingStatusUpdatedEvent(string serverCallId, string callId, string contentLocation, string documentId)
        {
            var eventGridEvent = new EventGridEvent($"/recording/call/{callId}/serverCallId/{serverCallId}",
                "Microsoft.Communication.RecordingFileStatusUpdated", "1.0",
                BinaryData.FromObjectAsJson(new
                {
                    recordingStorageInfo = new { recordingChunks = new[] { new { contentLocation, documentId } } }
                }));

            await _httpClient.PostAsJsonAsync("/api/recordingDone", new[] { eventGridEvent });
        }

        public async Task SendRecordingStartedEvent(string serverCallId)
        {
            var activeCall = CallContextService.GetActiveCall(serverCallId);
            var recordingStateChangedEvent = new CloudEvent(
                $"calling/callConnections/{activeCall.CallConnection.CallConnectionId}",
                "Microsoft.Communication.RecordingStateChanged", BinaryData.FromObjectAsJson(new
                {
                    state = RecordingState.Active.ToString(),
                    recordingId = activeCall.RecordingId,
                    recordingType = RecordingType.Acs.ToString(),
                    startDateTime = DateTimeOffset.UtcNow,
                    serverCallId,
                    callConnectionId = activeCall.CallConnection.CallConnectionId,
                    correlationId = activeCall.CallId,
                    operationContext = Guid.NewGuid().ToString()
                }), "application/json");

            await _httpClient.PostAsJsonAsync($"/api/calls/{activeCall.RecordingId}?callerId={activeCall.CallerId}",
                new[] { recordingStateChangedEvent });
        }
    }
}
