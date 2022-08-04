﻿using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Azure.Communication.CallingServer;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using IncomingCallRouting.Events;
using Newtonsoft.Json;

namespace IncomingCallRouting.Controllers
{
    [ApiController]
    public class IncomingCallController : Controller
    {
        private readonly CallingServerClient callingServerClient;
        CallConfiguration callConfiguration;
        EventAuthHandler eventAuthHandler;
        public IncomingCallController(IConfiguration configuration, ILogger<IncomingCallController> logger)
        {
            Logger.SetLoggerInstance(logger);
            callingServerClient = new CallingServerClient(new Uri(configuration["PmaUri"]), configuration["ResourceConnectionString"]);
            eventAuthHandler = new EventAuthHandler(configuration["SecretValue"]);
            callConfiguration = CallConfiguration.GetCallConfiguration(configuration, eventAuthHandler.GetSecretQuerystring);
        }

        /// Web hook to receive the incoming call Event
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("OnIncomingCall")]
        public IActionResult OnIncomingCall([FromBody] EventGridEvent[] eventGridEvents)
        {
            try
            {
                EventGridEvent eventGridEvent = eventGridEvents.FirstOrDefault();

                if (eventGridEvents.FirstOrDefault().EventType == SystemEventNames.EventGridSubscriptionValidation)
                {
                    var eventData = eventGridEvent.Data.ToObjectFromJson<SubscriptionValidationEventData>();
                    var responseData = new SubscriptionValidationResponse
                    {
                        ValidationResponse = eventData.ValidationCode
                    };

                    if (responseData.ValidationResponse != null)
                    {
                        return Ok(responseData);
                    }
                }
                else if (eventGridEvent.EventType.Equals("Microsoft.Communication.IncomingCall"))
                {
                    //Fetch incoming call context and ivr participant from request
                    var eventData = JsonConvert.DeserializeObject<IncomingCallData>(eventGridEvent.Data.ToString());
                    if (eventData != null)
                    {
                        var incomingCallContext = eventData.IncomingCallContext;
                        var ivrParticipant = eventData.To.RawId;
                        var from = eventData.From.RawId;

                        if ( (callConfiguration.IvrParticipants.Contains(ivrParticipant) || callConfiguration.IvrParticipants[0] == "*")
                            && callConfiguration.TargetParticipant != ivrParticipant)
                        {
                            _ = new IncomingCallHandler(callingServerClient, callConfiguration).Report(incomingCallContext);
                        }
 
                    }
                }
                return Ok();
            }
            catch (Exception ex)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, $"Fails in OnIncomingCall ---> {ex.Message}");
                return Json(new { Exception = ex });
            }
        }

        /// <summary>
        /// Extracting event from the json.
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("CallingServerAPICallBacks")]
        public IActionResult CallingServerAPICallBacks([FromBody] object request, [FromQuery] string secret)
        {
            try
            {
                if(eventAuthHandler.Authorize(secret))
                {
                    if (request != null)
                    {
                        EventDispatcher.Instance.ProcessNotification(request.ToString());
                    }
                    return Ok();
                }
                else
                {
                    return Unauthorized();
                }
            }
            catch (Exception ex)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, $"Fails with CallingServerAPICallBack ---> {ex.Message}");
                return Json(new { Exception = ex });
            }
        }

    }
}
