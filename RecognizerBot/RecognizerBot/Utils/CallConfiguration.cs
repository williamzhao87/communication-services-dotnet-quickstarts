﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace RecognizerBot.Utils
{
    /// <summary>
    /// Configuration assoociated with the call.
    /// </summary>
    public class CallConfiguration
    {
        private static CallConfiguration callConfiguration = null;
        public CallConfiguration(string connectionString, string appBaseUrl, string audioFileUri, string participant, string ivrParticipants, string consultTarget, string streamUri)
        {
            this.ConnectionString = connectionString;
            this.AppBaseUrl = appBaseUrl;
            this.AudioFileUrl = audioFileUri;
            this.AppCallbackUrl = $"{AppBaseUrl}/CallingServerAPICallBacks";
            this.TargetParticipant = participant;
            this.IvrParticipants = ivrParticipants.Split(',').Select(p => p.Trim()).ToList();
            this.ConsultTarget = consultTarget;
            this.StreamUri = streamUri;
        }

        public static CallConfiguration GetCallConfiguration(IConfiguration configuration)
        {
            if(callConfiguration == null)
            {
                callConfiguration = new CallConfiguration(configuration["ResourceConnectionString"],
                    configuration["AppCallBackUri"],
                    configuration["AudioFileUri"],
                    configuration["TargetParticipant"],
                    configuration["IVRParticipants"],
                    configuration["ConsultTarget"],
                    configuration["StreamUri"]);
            }

            return callConfiguration;
        }

        /// <summary>
        /// The connectionstring of Azure Communication Service resource.
        /// </summary>
        public string ConnectionString { get; private set; }

        /// <summary>
        /// The base url of the applicaiton.
        /// </summary>
        private string AppBaseUrl;

        /// <summary>
        /// The callback url of the application where notification would be received.
        /// </summary>
        public string AppCallbackUrl;

        /// <summary>
        /// The publicly available url of the audio file which would be played as a prompt.
        /// </summary>
        public string AudioFileUrl;

        /// <summary>
        /// The publicly available participant id to transfer the incoming call.
        /// </summary>
        public string TargetParticipant { get; private set; }


        /// <summary>
        /// The publicly available participant id to transfer the incoming call.
        /// </summary>
        public string ConsultTarget { get; private set; }

        /// <summary>
        /// The publicly available webhook url to stream media to.
        /// </summary>
        public string StreamUri { get; private set; }

        /// <summary>
        /// The publicly available participants id to transfer the incoming call.
        /// </summary>
        public List<string> IvrParticipants { get; private set; }
    }
}
