﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using IncomingCallRouting.Enums;
using IncomingCallRouting.Events;

namespace IncomingCallRouting
{
    /// <summary>
    /// Handling different callback events
    /// and perform operations
    /// </summary>

    using Azure.Communication;
    using Azure.Communication.CallingServer;
    using System;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    public class IncomingCallHandler
    {
        private readonly CallAutomationClient _callingServerClient;
        private readonly CallConfiguration _callConfiguration;
        private readonly CallRecording _callRecording;
        private CallConnection _callConnection;
        private CallConnectionProperties _callConnectionProperties;
        private CancellationTokenSource _reportCancellationTokenSource;
        private CancellationToken _reportCancellationToken;
        private readonly string _targetParticipant;
        private string _from;

        private TaskCompletionSource<bool> callEstablishedTask;
        private TaskCompletionSource<bool> playAudioCompletedTask;
        private TaskCompletionSource<bool> callTerminatedTask;
        private TaskCompletionSource<bool> toneReceivedCompleteTask;
        private TaskCompletionSource<bool> transferToParticipantCompleteTask;
        private readonly int maxRetryAttemptCount = 3;

        public IncomingCallHandler(CallAutomationClient callingServerClient, CallConfiguration callConfiguration)
        {
            _callConfiguration = callConfiguration;
            _callingServerClient = callingServerClient;
            _callRecording = callingServerClient.GetCallRecording();
            _targetParticipant = callConfiguration.TargetParticipant;
            _from = callConfiguration.IvrParticipants[0];
        }

        public async Task Report(string incomingCallContext)
        {
            _reportCancellationTokenSource = new CancellationTokenSource();
            _reportCancellationToken = _reportCancellationTokenSource.Token;

            try
            {
                // Answer Call
                var response = await _callingServerClient.AnswerCallAsync(incomingCallContext, new Uri(_callConfiguration.AppCallbackUrl));
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"AnswerCallAsync Response -----> {response.GetRawResponse()}");
                
                _callConnection = response.Value.CallConnection;
                _callConnectionProperties = response.Value.CallConnectionProperties;
                RegisterToCallStateChangeEvent(_callConnectionProperties.CallConnectionId);
                
                // //Wait for the call to get connected
                await callEstablishedTask.Task.ConfigureAwait(false);
                
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"Adding participant {_targetParticipant} to call");
                var addParticipant = await _callConnection.AddParticipantsAsync(
                    new List<CommunicationIdentifier>
                    {
                        GetIdentifier(_targetParticipant),
                    }, cancellationToken: _reportCancellationToken);
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"AddParticipant Response -----> {addParticipant.GetRawResponse()}");

                await PlayAudioAsync();

                // Logger.LogMessage(Logger.MessageType.INFORMATION, $"Tranferring call to participant {_targetParticipant}");
                // var transferToParticipantCompleted = await TransferToParticipant(_targetParticipant);
                // if (!transferToParticipantCompleted)
                // {
                //     await RetryTransferToParticipantAsync(async () => await TransferToParticipant(_targetParticipant));
                // }

                // Wait for the call to terminate
                await callTerminatedTask.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, $"Call ended unexpectedly, reason: {ex.Message}");
            }
        }

        private async Task RetryTransferToParticipantAsync(Func<Task<bool>> action)
        {
            int retryAttemptCount = 1;
            while (retryAttemptCount <= maxRetryAttemptCount)
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"Retrying Transfer participant attempt {retryAttemptCount} is in progress");
                var transferToParticipantResult = await action();
                if (transferToParticipantResult)
                {
                    return;
                }
                else
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, $"Retry transfer participant attempt {retryAttemptCount} has failed");
                    retryAttemptCount++;
                }
            }
        }

        private async Task PlayAudioAsync()
        {
            try
            {

                var playSource = new FileSource(new Uri(_callConfiguration.AudioFileUrl));

                var operationContext = Guid.NewGuid().ToString();
                var response = await _callConnection.GetCallMedia().PlayToAllAsync(playSource, new PlayOptions{ OperationContext = operationContext}).ConfigureAwait(false);
        
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"PlayAudioAsync response --> {response.Status}, Id: {response.ClientRequestId}");
        
                if (response.Status == 202)
                {
                    // listen to play audio events
                    RegisterToPlayAudioResultEvent(_callConnectionProperties.CallConnectionId);
        
                    var completedTask = await Task.WhenAny(playAudioCompletedTask.Task, Task.Delay(30 * 1000)).ConfigureAwait(false);
        
                    if (completedTask != playAudioCompletedTask.Task)
                    {
                        playAudioCompletedTask.TrySetResult(false);
                        toneReceivedCompleteTask.TrySetResult(false);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, "Play audio operation cancelled");
            }
            catch (Exception ex)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, $"Failure occured while playing audio on the call. Exception: {ex.Message}");
            }
        }

        private async Task HangupAsync()
        {
            if (_reportCancellationToken.IsCancellationRequested)
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, "Cancellation request, Hangup will not be performed");
                return;
            }

            Logger.LogMessage(Logger.MessageType.INFORMATION, "Performing Hangup operation");
            var hangupResponse = await _callConnection.HangUpAsync(false).ConfigureAwait(false);

            Logger.LogMessage(Logger.MessageType.INFORMATION, $"HangupAsync response --> {hangupResponse}");

        }

        private async Task CancelAllMediaOperations()
        {
            if (_reportCancellationToken.IsCancellationRequested)
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, "Cancellation request, CancelMediaProcessing will not be performed");
                return;
            }
        
            Logger.LogMessage(Logger.MessageType.INFORMATION, "Performing cancel media processing operation to stop playing audio");
        
            var operationContext = Guid.NewGuid().ToString();
            var response = await _callConnection.GetCallMedia().CancelAllMediaOperationsAsync(_reportCancellationToken).ConfigureAwait(false);
        
            Logger.LogMessage(Logger.MessageType.INFORMATION, $"PlayAudioAsync response --> {response.ContentStream}, " +
                $"Id: {response.Content}, Status: {response.Status}");
        }

        private void RegisterToCallStateChangeEvent(string callConnectionId)
        {
            callEstablishedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reportCancellationToken.Register(() => callEstablishedTask.TrySetCanceled());

            callTerminatedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            //Set the callback method
            var callStateChangeNotificaiton = new NotificationCallback((CallAutomationEventBase callEvent) =>
            {
                var callStateChanged = (CallConnected)callEvent;

                Logger.LogMessage(Logger.MessageType.INFORMATION, $"Call State changed to connected");

                if (callEvent is CallConnected callConnectedEvent)
                {
                    callEstablishedTask.TrySetResult(true);
                }
            });

            //Subscribe to the event
            var eventId = EventDispatcher.Instance.Subscribe(AcsEventType.CallConnected.ToString(), callConnectionId, callStateChangeNotificaiton);
        }

        private void RegisterToPlayAudioResultEvent(string operationContext)
        {
            playAudioCompletedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reportCancellationToken.Register(() => playAudioCompletedTask.TrySetCanceled());

            var playPromptResponseNotification = new NotificationCallback((CallAutomationEventBase callEvent) =>
            {
                Task.Run(() =>
                {
                    var playAudioResultEvent = (PlayAudioResultEvent)callEvent;
                    Logger.LogMessage(Logger.MessageType.INFORMATION, $"Play audio status: {playAudioResultEvent}");

                    playAudioCompletedTask.TrySetResult(true);
                    EventDispatcher.Instance.Unsubscribe(CallingServerEventType.PlayAudioResultEvent.ToString(), operationContext);
                });
            });

            //Subscribe to event
            EventDispatcher.Instance.Subscribe(CallingServerEventType.PlayAudioResultEvent.ToString(), operationContext, playPromptResponseNotification);
        }

        // private void RegisterToDtmfResultEvent(string callConnectionId)
        // {
        //     toneReceivedCompleteTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        //     var dtmfReceivedEvent = new NotificationCallback((CallAutomationEventBase callEvent) =>
        //     {
        //         Task.Run(async () =>
        //         {
        //             var toneReceivedEvent = (ToneReceivedEvent)callEvent;
        //             Logger.LogMessage(Logger.MessageType.INFORMATION, $"Tone received ---------> : {toneReceivedEvent.ToneInfo?.Tone}");
        //
        //             if (toneReceivedEvent?.ToneInfo?.Tone == ToneValue.Tone1)
        //             {
        //                 toneReceivedCompleteTask.TrySetResult(true);
        //             }
        //             else
        //             {
        //                 toneReceivedCompleteTask.TrySetResult(false);
        //             }
        //
        //             EventDispatcher.Instance.Unsubscribe(CallingServerEventType.ToneReceivedEvent.ToString(), callConnectionId);
        //             // cancel playing audio
        //             await CancelAllMediaOperations().ConfigureAwait(false);
        //         });
        //     });
        //     //Subscribe to event
        //     EventDispatcher.Instance.Subscribe(CallingServerEventType.ToneReceivedEvent.ToString(), callConnectionId, dtmfReceivedEvent);
        // }

        private CommunicationIdentifier GetIdentifier(String targetParticipant)
        {

            if (GetIdentifierKind(targetParticipant) == CommunicationIdentifierKind.UserIdentity)
            {
                return new CommunicationUserIdentifier(targetParticipant);
            }
            else if (GetIdentifierKind(targetParticipant) == CommunicationIdentifierKind.PhoneIdentity)
            {
                return new PhoneNumberIdentifier(targetParticipant);
            }
            else if (GetIdentifierKind(targetParticipant) == CommunicationIdentifierKind.TeamsIdentity)
            {
                return new MicrosoftTeamsUserIdentifier(targetParticipant);
            }
            else
            {
                return null;
            }
        }

        private async Task<bool> TransferToParticipant(string targetParticipant, string transfereeCallerId = null)
        {
            transferToParticipantCompleteTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var identifier = GetIdentifier(targetParticipant);

            if (identifier == null)
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, "Unknown identity provided. Enter valid phone number or communication user id");
                return true;
            }
            var operationContext = Guid.NewGuid().ToString();

            var response = await _callConnection.TransferCallToParticipantAsync(identifier,
                new TransferCallToParticipantOptions
                {
                    OperationContext = operationContext,
                    SourceCallerId = transfereeCallerId == null ? null : new PhoneNumberIdentifier(transfereeCallerId),
                    UserToUserInformation = "user1",
                });

            var transferToParticipantCompleted = await transferToParticipantCompleteTask.Task.ConfigureAwait(false);
            return transferToParticipantCompleted;
        }

        private void RegisterToTransferParticipantsResultEvent(string operationContext)
        {
            var transferToParticipantReceivedEvent = new NotificationCallback(async (CallAutomationEventBase callEvent) =>
            {
                var transferParticipantUpdatedEvent = (ParticipantsUpdated)callEvent;
                if (transferParticipantUpdatedEvent.CallConnectionId != null)
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, $"Transfer participant callconnection ID - {transferParticipantUpdatedEvent.CallConnectionId}");
                    EventDispatcher.Instance.Unsubscribe(CallingServerEventType.ParticipantsUpdatedEvent.ToString(), operationContext);

                    Logger.LogMessage(Logger.MessageType.INFORMATION, "Sleeping for 60 seconds before proceeding further");
                    await Task.Delay(60 * 1000);

                    transferToParticipantCompleteTask.TrySetResult(true);
                }
                else
                {
                    transferToParticipantCompleteTask.TrySetResult(false);
                }
            });

            //Subscribe to event
            EventDispatcher.Instance.Subscribe(CallingServerEventType.ParticipantsUpdatedEvent.ToString(), operationContext, transferToParticipantReceivedEvent);
        }

        private CommunicationIdentifierKind GetIdentifierKind(string participantnumber)
        {
            //checks the identity type returns as string
            return Regex.Match(participantnumber, Constants.userIdentityRegex, RegexOptions.IgnoreCase).Success ? CommunicationIdentifierKind.UserIdentity :
                   Regex.Match(participantnumber, Constants.phoneIdentityRegex, RegexOptions.IgnoreCase).Success ? CommunicationIdentifierKind.PhoneIdentity :
                   Regex.Match(participantnumber, Constants.teamsIdentityRegex, RegexOptions.IgnoreCase).Success ? CommunicationIdentifierKind.TeamsIdentity :
                   CommunicationIdentifierKind.UnknownIdentity;
        }
    }
}
