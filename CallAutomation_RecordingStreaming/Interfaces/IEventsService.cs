using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.CognitiveServices.Speech;
using RecordingStreaming.Models;

namespace RecordingStreaming.Interfaces
{
    public interface IEventsService
    {
        Task SendRecordingStatusUpdatedEvent(string serverCallId, string callId, string contentLocation, string documentId);

        Task SendRecordingStartedEvent(string serverCallId);
    }
}
