// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Hub.Core.Twin
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Device;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Identity;
    using Microsoft.Azure.Devices.Edge.Storage;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public class StoringTwinManager : ITwinManager
    {
        static readonly TimeSpan DefaultMinTwinSyncPeriod = TimeSpan.FromMinutes(2);
        readonly IMessageConverter<TwinCollection> twinCollectionConverter;
        readonly IMessageConverter<Twin> twinConverter;
        readonly IConnectionManager connectionManager;
        readonly IValidator<TwinCollection> reportedPropertiesValidator;
        readonly IReportedPropertiesStore reportedPropertiesStore;
        readonly AsyncLockProvider<string> twinStoreLock = new AsyncLockProvider<string>(10);
        readonly AsyncLockProvider<string> reportedPropertiesStoreLock = new AsyncLockProvider<string>(10);
        readonly ITwinStore twinStore;
        readonly ICloudSync cloudSync;
        readonly TimeSpan minTwinSyncPeriod;
        readonly ConcurrentDictionary<string, DateTime> twinSyncTime = new ConcurrentDictionary<string, DateTime>();

        internal StoringTwinManager(
            IConnectionManager connectionManager,
            IMessageConverter<TwinCollection> twinCollectionConverter,
            IMessageConverter<Twin> twinConverter,
            IValidator<TwinCollection> reportedPropertiesValidator,
            ITwinStore twinStore,
            IReportedPropertiesStore reportedPropertiesStore,
            ICloudSync cloudSync,
            IDeviceConnectivityManager deviceConnectivityManager,
            TimeSpan minTwinSyncPeriod)
        {
            Preconditions.CheckNotNull(twinStore, nameof(twinStore));
            Preconditions.CheckNotNull(deviceConnectivityManager, nameof(deviceConnectivityManager));
            this.connectionManager = Preconditions.CheckNotNull(connectionManager, nameof(connectionManager));
            this.twinCollectionConverter = Preconditions.CheckNotNull(twinCollectionConverter, nameof(twinCollectionConverter));
            this.twinConverter = Preconditions.CheckNotNull(twinConverter, nameof(twinConverter));
            this.cloudSync = Preconditions.CheckNotNull(cloudSync, nameof(cloudSync));
            this.twinStore = Preconditions.CheckNotNull(twinStore, nameof(twinStore));
            this.reportedPropertiesStore = Preconditions.CheckNotNull(reportedPropertiesStore, nameof(reportedPropertiesStore));
            this.reportedPropertiesValidator = reportedPropertiesValidator;
            this.minTwinSyncPeriod = minTwinSyncPeriod;

            deviceConnectivityManager.DeviceConnected += (_, __) => this.DeviceConnectedCallback();

            Events.Initialized();
        }

        public static ITwinManager Create(
            IConnectionManager connectionManager,
            IMessageConverterProvider messageConverterProvider,
            IEntityStore<string, TwinStoreEntity> entityStore,
            IDeviceConnectivityManager deviceConnectivityManager,
            IValidator<TwinCollection> reportedPropertiesValidator,
            Option<TimeSpan> minTwinSyncPeriod,
            Option<TimeSpan> reportedPropertiesSyncFrequency)
        {
            Preconditions.CheckNotNull(connectionManager, nameof(connectionManager));
            Preconditions.CheckNotNull(messageConverterProvider, nameof(messageConverterProvider));
            Preconditions.CheckNotNull(entityStore, nameof(entityStore));
            Preconditions.CheckNotNull(deviceConnectivityManager, nameof(deviceConnectivityManager));
            Preconditions.CheckNotNull(reportedPropertiesValidator, nameof(reportedPropertiesValidator));

            IMessageConverter<TwinCollection> twinCollectionConverter = messageConverterProvider.Get<TwinCollection>();
            IMessageConverter<Twin> twinConverter = messageConverterProvider.Get<Twin>();
            ICloudSync cloudSync = new CloudSync(connectionManager, twinCollectionConverter, twinConverter);
            var twinManager = new StoringTwinManager(
                connectionManager,
                twinCollectionConverter,
                twinConverter,
                reportedPropertiesValidator,
                new TwinStore(entityStore),
                new ReportedPropertiesStore(entityStore, cloudSync, reportedPropertiesSyncFrequency),
                cloudSync,
                deviceConnectivityManager,
                minTwinSyncPeriod.GetOrElse(DefaultMinTwinSyncPeriod));

            return twinManager;
        }

        public async Task<IMessage> GetTwinAsync(string id)
        {
            Preconditions.CheckNotNull(id, nameof(id));

            string childAgentTwin = "{\"deviceId\":\"richmaEdge2\",\"moduleId\":\"$edgeAgent\",\"etag\":\"AAAAAAAAAAM=\",\"version\":4,\"deviceEtag\":\"\\\"MTM3OTY4Nzc2\\\"\",\"status\":\"enabled\",\"statusUpdateTime\":\"0001-01-01T00:00:00Z\",\"connectionState\":\"Disconnected\",\"lastActivityTime\":\"0001-01-01T00:00:00Z\",\"cloudToDeviceMessageCount\":0,\"authenticationType\":\"sas\",\"x509Thumbprint\":{\"primaryThumbprint\":null,\"secondaryThumbprint\":null},\"properties\":{\"desired\":{\"schemaVersion\":\"1.0\",\"runtime\":{\"type\":\"docker\",\"settings\":{\"minDockerVersion\":\"v1.25\",\"loggingOptions\":\"\",\"registryCredentials\":{\"rc1\":{\"username\":\"EdgeBuilds\",\"password\":\"RsF1n6BENWkTYuG+LK7oSpJ2KMHoONhl\",\"address\":\"edgebuilds.azurecr.io\"}}}},\"systemModules\":{\"edgeAgent\":{\"type\":\"docker\",\"settings\":{\"image\":\"edgebuilds.azurecr.io/microsoft/azureiotedge-agent:20200701.2-linux-amd64\",\"createOptions\":\"\"},\"env\":{\"RuntimeLogLevel\":{\"value\":\"Debug\"},\"ConfigRefreshFrequencySecs\":{\"value\":900}}},\"edgeHub\":{\"type\":\"docker\",\"settings\":{\"image\":\"edgebuilds.azurecr.io/microsoft/azureiotedge-hub:20200701.2-linux-amd64\",\"createOptions\":\"{\\\"HostConfig\\\":{\\\"PortBindings\\\": {\\\"8883/tcp\\\": [{\\\"HostPort\\\": \\\"8883\\\"}],\\\"443/tcp\\\": [{\\\"HostPort\\\": \\\"443\\\"}],\\\"5671/tcp\\\": [{\\\"HostPort\\\": \\\"5671\\\"}]}}}\"},\"env\":{\"UpstreamProtocol\":{\"value\":\"Amqp\"}},\"status\":\"running\",\"restartPolicy\":\"always\"}},\"modules\":{\"tempSensor\":{\"version\":\"1.0\",\"type\":\"docker\",\"status\":\"running\",\"restartPolicy\":\"always\",\"env\":{\"MessageCount\":{\"value\":\"7200\"}},\"settings\":{\"image\":\"mcr.microsoft.com/azureiotedge-simulated-temperature-sensor:1.0\",\"createOptions\":\"\"}},\"DirectMethodReceiver\":{\"version\":\"1.0\",\"type\":\"docker\",\"status\":\"running\",\"restartPolicy\":\"always\",\"env\":{\"ClientTransportType\":{\"value\":\"Amqp_Tcp_Only\"}},\"settings\":{\"image\":\"edgebuilds.azurecr.io/microsoft/azureiotedge-direct-method-receiver:20200701.2-linux-amd64\",\"createOptions\":\"\"}}},\"$metadata\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"schemaVersion\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"runtime\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"type\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"settings\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"minDockerVersion\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"loggingOptions\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"registryCredentials\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"rc1\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"username\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"password\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"address\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}}}},\"systemModules\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"edgeAgent\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"type\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"settings\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"image\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"createOptions\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}},\"env\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"RuntimeLogLevel\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"value\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}},\"ConfigRefreshFrequencySecs\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"value\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}}},\"edgeHub\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"type\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"settings\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"image\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"createOptions\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}},\"env\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"UpstreamProtocol\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"value\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}},\"status\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"restartPolicy\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}},\"modules\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"tempSensor\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"version\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"type\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"status\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"restartPolicy\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"env\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"MessageCount\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"value\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}},\"settings\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"image\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"createOptions\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}},\"DirectMethodReceiver\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"version\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"type\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"status\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"restartPolicy\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"env\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"ClientTransportType\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"value\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}},\"settings\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"image\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"createOptions\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}}}},\"$version\":3},\"reported\":{\"$metadata\":{\"$lastUpdated\":\"0001-01-01T00:00:00Z\"},\"$version\":1}}}";
            string childHubTwin = "{\"deviceId\":\"richmaEdge2\",\"moduleId\":\"$edgeHub\",\"etag\":\"AAAAAAAAAAM=\",\"version\":4,\"deviceEtag\":\"\\\"MTM3OTY4Nzc2\\\"\",\"status\":\"enabled\",\"statusUpdateTime\":\"0001-01-01T00:00:00Z\",\"connectionState\":\"Disconnected\",\"lastActivityTime\":\"0001-01-01T00:00:00Z\",\"cloudToDeviceMessageCount\":0,\"authenticationType\":\"sas\",\"x509Thumbprint\":{\"primaryThumbprint\":null,\"secondaryThumbprint\":null},\"properties\":{\"desired\":{\"schemaVersion\":\"1.0\",\"routes\":{\"route\":\"FROM /messages/* INTO $upstream\",\"SimulatedTemperatureSensorToIoTHub\":\"FROM /messages/modules/SimulatedTemperatureSensor/* INTO $upstream\"},\"storeAndForwardConfiguration\":{\"timeToLiveSecs\":86400},\"$metadata\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"schemaVersion\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"routes\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"route\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3},\"SimulatedTemperatureSensorToIoTHub\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}},\"storeAndForwardConfiguration\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3,\"timeToLiveSecs\":{\"$lastUpdated\":\"2020-07-02T01:19:03.3227071Z\",\"$lastUpdatedVersion\":3}}},\"$version\":3},\"reported\":{\"$metadata\":{\"$lastUpdated\":\"0001-01-01T00:00:00Z\"},\"$version\":1}}}";

            Option<Twin> twinOption;
            if (id == "richmaEdge2/$edgeAgent")
            {
                twinOption = Option.Some(JsonConvert.DeserializeObject<Twin>(childAgentTwin));
            }
            else if (id == "richmaEdge2/$edgeHub")
            {
                twinOption = Option.Some(JsonConvert.DeserializeObject<Twin>(childHubTwin));
            }
            else
            {
                twinOption = await this.cloudSync.GetTwin(id);
            }

            Twin twin = await twinOption
                .Map(
                    async t =>
                    {
                        Events.GotTwinFromCloud(id);
                        await this.StoreTwinInStore(id, t);
                        return t;
                    })
                .GetOrElse(
                    async () =>
                    {
                        Events.GettingTwinFromStore(id);
                        Option<Twin> storedTwin = await this.twinStore.Get(id);
                        return storedTwin.GetOrElse(() => new Twin());
                    });
            return this.twinConverter.ToMessage(twin);
        }

        public async Task<Option<IMessage>> GetCachedTwinAsync(string id)
        {
            Option<Twin> storedTwin = await this.twinStore.Get(id);
            return storedTwin.Map(twin => this.twinConverter.ToMessage(twin));
        }

        public async Task UpdateDesiredPropertiesAsync(string id, IMessage twinCollection)
        {
            Preconditions.CheckNotNull(id, nameof(id));
            Preconditions.CheckNotNull(twinCollection, nameof(twinCollection));

            TwinCollection patch = this.twinCollectionConverter.FromMessage(twinCollection);
            Events.UpdatingDesiredProperties(id, patch);

            Option<Twin> storeTwin = await this.twinStore.Get(id);
            await storeTwin
                .Filter(t => t.Properties?.Desired?.Version + 1 != patch.Version)
                .Map(t => this.SyncTwinAndSendDesiredPropertyUpdates(id, t))
                .GetOrElse(
                    async () =>
                    {
                        await this.twinStore.UpdateDesiredProperties(id, patch);
                        await this.SendPatchToDevice(id, twinCollection);
                    });
        }

        public async Task UpdateReportedPropertiesAsync(string id, IMessage twinCollection)
        {
            Preconditions.CheckNotNull(id, nameof(id));
            Preconditions.CheckNotNull(twinCollection, nameof(twinCollection));

            Events.UpdatingReportedProperties(id);
            TwinCollection patch = this.twinCollectionConverter.FromMessage(twinCollection);
            this.reportedPropertiesValidator.Validate(patch);
            using (await this.reportedPropertiesStoreLock.GetLock(id).LockAsync())
            {
                await this.twinStore.UpdateReportedProperties(id, patch);
                await this.reportedPropertiesStore.Update(id, patch);
            }

            this.reportedPropertiesStore.InitSyncToCloud(id);
        }

        async void DeviceConnectedCallback()
        {
            try
            {
                Events.HandlingDeviceConnectedCallback();
                IEnumerable<IIdentity> connectedClients = this.connectionManager.GetConnectedClients();
                foreach (IIdentity client in connectedClients)
                {
                    string id = client.Id;
                    try
                    {
                        await this.reportedPropertiesStore.SyncToCloud(id);
                        await this.HandleDesiredPropertiesUpdates(id);
                    }
                    catch (Exception e)
                    {
                        Events.ErrorHandlingDeviceConnected(e, id);
                    }
                }
            }
            catch (Exception ex)
            {
                Events.ErrorInDeviceConnectedCallback(ex);
            }
        }

        async Task SyncTwinAndSendDesiredPropertyUpdates(string id, Twin storeTwin)
        {
            Option<Twin> twinOption = await this.cloudSync.GetTwin(id);
            await twinOption.ForEachAsync(
                async cloudTwin =>
                {
                    Events.UpdatingTwinOnDeviceConnect(id);
                    await this.StoreTwinInStore(id, cloudTwin);

                    JObject diffPatch = JsonEx.Diff(JToken.FromObject(storeTwin.Properties.Desired), JToken.FromObject(cloudTwin.Properties.Desired));
                    if (diffPatch != null && diffPatch.HasValues)
                    {
                        var patch = new TwinCollection(diffPatch.ToString());
                        IMessage patchMessage = this.twinCollectionConverter.ToMessage(patch);
                        await this.SendPatchToDevice(id, patchMessage);
                    }
                });
        }

        async Task HandleDesiredPropertiesUpdates(string id)
        {
            Option<Twin> storeTwin = await this.twinStore.Get(id);
            if (!storeTwin.HasValue && !this.connectionManager.CheckClientSubscription(id, DeviceSubscription.DesiredPropertyUpdates))
            {
                Events.NoTwinUsage(id);
            }
            else
            {
                await storeTwin.ForEachAsync(
                    async twin =>
                    {
                        if (!this.twinSyncTime.TryGetValue(id, out DateTime syncTime) ||
                            DateTime.UtcNow - syncTime > this.minTwinSyncPeriod)
                        {
                            await this.SyncTwinAndSendDesiredPropertyUpdates(id, twin);
                        }
                        else
                        {
                            Events.TwinSyncedRecently(id, syncTime, this.minTwinSyncPeriod);
                        }
                    });
            }
        }

        Task SendPatchToDevice(string id, IMessage twinCollection)
        {
            Events.SendDesiredPropertyUpdates(id);
            bool hasDesiredPropertyUpdatesSubscription = this.connectionManager.CheckClientSubscription(id, DeviceSubscription.DesiredPropertyUpdates);
            if (hasDesiredPropertyUpdatesSubscription)
            {
                Events.SendDesiredPropertyUpdates(id);
                Option<IDeviceProxy> deviceProxyOption = this.connectionManager.GetDeviceConnection(id);
                return deviceProxyOption.ForEachAsync(deviceProxy => deviceProxy.OnDesiredPropertyUpdates(twinCollection));
            }

            Events.SendDesiredPropertyUpdates(id);
            return Task.CompletedTask;
        }

        async Task StoreTwinInStore(string id, Twin twin)
        {
            using (await this.twinStoreLock.GetLock(id).LockAsync())
            {
                await this.twinStore.Update(id, twin);
                this.twinSyncTime.AddOrUpdate(id, DateTime.UtcNow, (_, __) => DateTime.UtcNow);
            }
        }

        static class Events
        {
            const int IdStart = HubCoreEventIds.TwinManager;
            static readonly ILogger Log = Logger.Factory.CreateLogger<StoringTwinManager>();

            enum EventIds
            {
                ErrorInDeviceConnectedCallback = IdStart,
                StoringTwinManagerCreated,
                NoTwinUsage,
                UpdatingTwinOnDeviceConnect,
                SendDesiredPropertyUpdates,
                UpdatingDesiredProperties,
                GettingTwinFromStore,
                GotTwinFromTwin,
                TwinSyncedRecently,
                UpdatingReportedProperties,
                ErrorHandlingDeviceConnected,
                HandlingDeviceConnectedCallback,
                Initialized
            }

            public static void Initialized()
            {
                Log.LogInformation((int)EventIds.Initialized, "Initialized storing twin manager");
            }

            public static void ErrorInDeviceConnectedCallback(Exception ex)
            {
                Log.LogWarning((int)EventIds.ErrorInDeviceConnectedCallback, ex, "Error in device connected callback");
            }

            public static void StoringTwinManagerCreated()
            {
                Log.LogInformation((int)EventIds.StoringTwinManagerCreated, $"Storing twin manager created");
            }

            public static void HandlingDeviceConnectedCallback()
            {
                Log.LogInformation((int)EventIds.HandlingDeviceConnectedCallback, $"Received device connected callback");
            }

            public static void ErrorHandlingDeviceConnected(Exception ex, string id)
            {
                Log.LogWarning((int)EventIds.ErrorHandlingDeviceConnected, ex, $"Error handling device connected event for {id}");
            }

            public static void UpdatingReportedProperties(string id)
            {
                Log.LogDebug((int)EventIds.UpdatingReportedProperties, $"Updating reported properties for {id}");
            }

            public static void UpdatingDesiredProperties(string id, TwinCollection patch)
            {
                Log.LogDebug((int)EventIds.UpdatingDesiredProperties, $"Received desired property updates for {id} with version {patch.Version}");
            }

            public static void SendDesiredPropertyUpdates(string id)
            {
                Log.LogDebug((int)EventIds.SendDesiredPropertyUpdates, $"Sending desired property updates to {id}");
            }

            public static void GettingTwinFromStore(string id)
            {
                Log.LogDebug((int)EventIds.GettingTwinFromStore, $"Getting twin for {id} from store");
            }

            public static void GotTwinFromCloud(string id)
            {
                Log.LogDebug((int)EventIds.GotTwinFromTwin, $"Got twin for {id} from cloud");
            }

            public static void TwinSyncedRecently(string id, DateTime syncTime, TimeSpan timeSpan)
            {
                Log.LogDebug((int)EventIds.TwinSyncedRecently, $"Twin for {id} synced at {syncTime} which is sooner than twin sync period {timeSpan.TotalSeconds} secs, skipping syncing twin");
            }

            public static void NoTwinUsage(string id)
            {
                Log.LogDebug((int)EventIds.NoTwinUsage, $"Not syncing twin on device connect for {id} as the twin does not exist in the store and client does not subscribe to twin change notifications");
            }

            public static void UpdatingTwinOnDeviceConnect(string id)
            {
                Log.LogDebug((int)EventIds.UpdatingTwinOnDeviceConnect, $"Updated twin for {id} on device connect event");
            }
        }
    }
}
