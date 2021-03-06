﻿using static Com.Amalto.Imdg.Cm.CommonMessage.Types;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Com.Amalto.Imdg.Cm;
using Hazelcast.Client;
using Hazelcast.Config;
using Hazelcast.Core;
using Library.Models;
using Library.Serializers;
using Newtonsoft.Json;

namespace Library {
    /// Platform 6 service helpers
    public class Service {
        /// The identifier of the service
        private string _id;
        /// The service's id formatted into a key
        private string _idKey;
        /// The identifier of the node
        private string _nodeId;

        /// Hazelcast client instance
        public IHazelcastInstance Client;
        /// Platform 6 service instance
        public Task Deployed;

        /// <summary>Create an instance of Platform 6 service</summary>
        /// <param name="parameters">Deployment parameters</param>
        public Service(DeployParameters parameters) {
            _id = parameters.Id;
            _idKey = Constants.SenderIdPrefix + parameters.Id;
            Deployed = DeployService(parameters);
        }

        /// <summary>Create a Hazelcast client</summary>
        /// <returns>Promise of void</returns>
        private async Task CreateHazelcastClient() {
            Environment.SetEnvironmentVariable("hazelcast.logging.level", "info");
            Environment.SetEnvironmentVariable("hazelcast.logging.type", "console");

            var config = new ClientConfig();
            var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? "localhost";
            var port = Environment.GetEnvironmentVariable("PORT") != null ? Convert.ToUInt32(Environment.GetEnvironmentVariable("PORT"), 10) : 5900;

            var serializerConfig = new SerializerConfig();
            serializerConfig.SetImplementation(new Message()).SetTypeClass(typeof(CommonMessage));

            config.GetSerializationConfig().AddSerializerConfig(serializerConfig);
            config.GetNetworkConfig().AddAddress(hostname + ":" + port);

            Client = Task.Run(() => HazelcastClient.NewHazelcastClient(config)).Result;
            _nodeId = Client.GetLocalEndpoint().GetUuid();
        }

        /// <summary>Deploy the service</summary>
        /// <param name="parameters">Deployment parameters</param>
        /// <returns>Response of the service deployment</returns>
        private async Task DeployService(DeployParameters parameters) {
            if (Client == null) await CreateHazelcastClient();

            await CallService(new CallServiceParameters {
                ReceiverId = Constants.ServiceManagerId,
                Action = Constants.ActionDeploy,
                Headers = new List<Header> {
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "node.id", _nodeId),
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "service.id", _id),
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "service.path", parameters.Path),
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "service.ctx", parameters.BasePath),
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "service.version", parameters.Versions.Server),
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "service.ui.version", parameters.Versions.Client),
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "service.ui", JsonConvert.SerializeObject(parameters.Ui))
                }
            });
        }

        /// <summary>Undeploy the service</summary>
        /// <returns>Response of the service undeployment</returns>
        public async Task UndeployService() {
            await CallService(new CallServiceParameters {
                ReceiverId = Constants.ServiceManagerId,
                Action = Constants.ActionUnDeploy,
                Headers = new List<Header> {
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "node.id", _nodeId),
                    BusConnection.CreateHeader(Constants.Platform6AppKey + "service.id", _id)
                }
            });
        }

        /// <summary>Send a request to another service</summary>
        /// <param name="parameters">Required parameters for the request</param>
        /// <returns>Response of the service</returns>
        public async Task<CommonMessage> CallService(CallServiceParameters parameters) {
            var receiverId = parameters.ReceiverId;
            var headers = new List<Header>();
            var attachments = parameters.Attachments ?? new List<Attachment>();

            if (parameters.Username != null) headers.Add(BusConnection.CreateHeader(Constants.RequestPrefix + "user", parameters.Username));
            if (parameters.Action != null) headers.Add(BusConnection.CreateHeader(Constants.RequestPrefix +  "action", parameters.Action));
            if (parameters.Headers != null) headers.AddRange(parameters.Headers);

            var commonMessage = Task.Run(() => BusConnection.CreateCommonMessage(_idKey, parameters.ReceiverId, headers, attachments)).Result;
            return await SendCommonMessage(receiverId, commonMessage);
        }

        /// <summary>Send a message to another service</summary>
        /// <param name="receiverId">Identifier of the service receiving the message</param>
        /// <param name="commonMessage">Message sent</param>
        /// <returns>Response of the common message sent</returns>
        public async Task<CommonMessage> SendCommonMessage(string receiverId, CommonMessage commonMessage) {
            var receiverIdKey = Constants.ReceiverIdPrefix + receiverId;
            var request = Client.GetQueue<CommonMessage>(receiverIdKey);
            var isSent = Task.Run(() => request.Offer(commonMessage)).Result;

            if (!isSent) throw new Exception("Unable to send the common message with id " + commonMessage.Id + "!");

            BusConnection.DisplayCommonMessage(commonMessage);

            var response = Task.Run(() => Client.GetQueue<CommonMessage>(_idKey).Take()).Result;

            BusConnection.DisplayCommonMessage(response);

            if (!response.Id.Equals(commonMessage.Id))
                throw new Exception("Common message response's id " + response.Id + " is not the same as the common message request's id " + commonMessage.Id + "!");

            return response;
        }
    }
}
