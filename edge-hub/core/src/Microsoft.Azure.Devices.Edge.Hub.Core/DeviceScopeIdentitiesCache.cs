// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Hub.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Identity.Service;
    using Microsoft.Azure.Devices.Edge.Storage;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Json;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Nito.AsyncEx;
    using Org.BouncyCastle.Asn1;

    public sealed class DeviceScopeIdentitiesCache : IDeviceScopeIdentitiesCache
    {
        readonly IServiceProxy serviceProxy;
        readonly IKeyValueStore<string, string> encryptedStore;
        readonly IServiceIdentityHierarchy serviceIdentityHierarchy;
        readonly Timer refreshCacheTimer;
        readonly TimeSpan periodicRefreshRate;
        readonly TimeSpan refreshDelay;
        readonly AsyncAutoResetEvent refreshCacheSignal = new AsyncAutoResetEvent();
        readonly AsyncManualResetEvent refreshCacheCompleteSignal = new AsyncManualResetEvent();
        readonly object refreshCacheLock = new object();

        Task refreshCacheTask;
        DateTime cacheLastRefreshTime;
        Dictionary<string, DateTime> identitiesLastRefreshTime;

        DeviceScopeIdentitiesCache(
            IServiceIdentityHierarchy serviceIdentityHierarchy,
            IServiceProxy serviceProxy,
            IKeyValueStore<string, string> encryptedStorage,
            IDictionary<string, StoredServiceIdentity> initialCache,
            TimeSpan periodicRefreshRate,
            TimeSpan refreshDelay)
        {
            this.serviceIdentityHierarchy = serviceIdentityHierarchy;
            this.serviceProxy = serviceProxy;
            this.encryptedStore = encryptedStorage;
            this.periodicRefreshRate = periodicRefreshRate;
            this.refreshDelay = refreshDelay;
            this.refreshCacheTimer = new Timer(this.RefreshCache, null, TimeSpan.Zero, this.periodicRefreshRate);
            this.identitiesLastRefreshTime = new Dictionary<string, DateTime>();
            this.cacheLastRefreshTime = DateTime.MinValue;

            // Populate the serviceIdentityHierarchy
            foreach (KeyValuePair<string, StoredServiceIdentity> kvp in initialCache)
            {
                kvp.Value.ServiceIdentity.ForEach(serviceIdentity => this.serviceIdentityHierarchy.InsertOrUpdate(serviceIdentity).Wait());
            }
        }

        public event EventHandler<string> ServiceIdentityRemoved;

        public event EventHandler<ServiceIdentity> ServiceIdentityUpdated;

        public static async Task<DeviceScopeIdentitiesCache> Create(
            IServiceIdentityHierarchy serviceIdentityHierarchy,
            IServiceProxy serviceProxy,
            IKeyValueStore<string, string> encryptedStorage,
            TimeSpan refreshRate,
            TimeSpan refreshDelay)
        {
            Preconditions.CheckNotNull(serviceProxy, nameof(serviceProxy));
            Preconditions.CheckNotNull(encryptedStorage, nameof(encryptedStorage));
            Preconditions.CheckNotNull(serviceIdentityHierarchy, nameof(serviceIdentityHierarchy));
            IDictionary<string, StoredServiceIdentity> cache = await ReadCacheFromStore(encryptedStorage);
            var deviceScopeIdentitiesCache = new DeviceScopeIdentitiesCache(serviceIdentityHierarchy, serviceProxy, encryptedStorage, cache, refreshRate, refreshDelay);
            Events.Created();
            return deviceScopeIdentitiesCache;
        }

        public void InitiateCacheRefresh()
        {
            Events.ReceivedRequestToRefreshCache();

            TimeSpan timeSinceLastRefresh = DateTime.UtcNow - this.cacheLastRefreshTime;

            // Only refresh the cache if we haven't done so recently
            if (timeSinceLastRefresh > this.refreshDelay)
            {
                this.refreshCacheCompleteSignal.Reset();
                this.refreshCacheSignal.Set();

                // Update the cache refresh timestamp
                this.cacheLastRefreshTime = DateTime.UtcNow;
            }
            else
            {
                Events.SkipRefreshCache(this.cacheLastRefreshTime, this.refreshDelay);
                this.refreshCacheCompleteSignal.Set();
            }
        }

        public Task WaitForCacheRefresh(TimeSpan timeout) => this.refreshCacheCompleteSignal.WaitAsync(timeout);

        public async Task RefreshServiceIdentity(string id)
        {
            try
            {
                if (await this.ShouldRefreshIdentity(id))
                {
                    Events.RefreshingServiceIdentity(id);
                    Option<ServiceIdentity> serviceIdentity = await this.GetServiceIdentityFromService(id);
                    await serviceIdentity
                        .Map(s => this.HandleNewServiceIdentity(s))
                        .GetOrElse(() => this.HandleNoServiceIdentity(id));

                    // Update the timestamp for this identity so we
                    // don't repeatedly refresh the same identity
                    // in rapid succession.
                    this.identitiesLastRefreshTime[id] = DateTime.UtcNow;
                }
                else
                {
                    Events.SkipRefreshServiceIdentity(id, this.identitiesLastRefreshTime[id], this.refreshDelay);
                }
            }
            catch (Exception e)
            {
                Events.ErrorRefreshingCache(e, id);
            }
        }

        public async Task RefreshServiceIdentities(IEnumerable<string> ids)
        {
            List<string> idList = Preconditions.CheckNotNull(ids, nameof(ids)).ToList();
            foreach (string id in idList)
            {
                await this.RefreshServiceIdentity(id);
            }
        }

        public async Task RefreshAuthChain(string authChain)
        {
            Preconditions.CheckNonWhiteSpace(authChain, nameof(authChain));

            // Refresh each element in the auth-chain
            string[] ids = AuthChainHelpers.GetAuthChainIds(authChain);
            await this.RefreshServiceIdentities(ids);
        }

        public async Task<Option<ServiceIdentity>> GetServiceIdentity(string id)
        {
            Preconditions.CheckNonWhiteSpace(id, nameof(id));
            Events.GettingServiceIdentity(id);
            return await this.GetServiceIdentityInternal(id);
        }

        public Task<Option<string>> GetAuthChain(string targetId) => this.serviceIdentityHierarchy.GetAuthChain(targetId);

        public async Task<IList<ServiceIdentity>> GetDevicesAndModulesInTargetScopeAsync(string targetDeviceId) => await this.serviceIdentityHierarchy.GetImmediateChildren(targetDeviceId);

        public void Dispose()
        {
            this.encryptedStore?.Dispose();
            this.refreshCacheTimer?.Dispose();
            this.refreshCacheTask?.Dispose();
        }

        internal Task<Option<ServiceIdentity>> GetServiceIdentityFromService(string id)
        {
            // If it is a module id, it will have the format "deviceId/moduleId"
            string[] parts = id.Split('/');
            if (parts.Length == 2)
            {
                return this.serviceProxy.GetServiceIdentity(parts[0], parts[1]);
            }
            else
            {
                return this.serviceProxy.GetServiceIdentity(id);
            }
        }

        static async Task<IDictionary<string, StoredServiceIdentity>> ReadCacheFromStore(IKeyValueStore<string, string> encryptedStore)
        {
            IDictionary<string, StoredServiceIdentity> cache = new Dictionary<string, StoredServiceIdentity>();
            await encryptedStore.IterateBatch(
                int.MaxValue,
                (key, value) =>
                {
                    cache.Add(key, JsonConvert.DeserializeObject<StoredServiceIdentity>(value));
                    return Task.CompletedTask;
                });
            return cache;
        }

        async Task<bool> ShouldRefreshIdentity(string id)
        {
            bool hasRefreshed = this.identitiesLastRefreshTime.TryGetValue(id, out DateTime lastRefreshTime);

            // Only refresh an identity if we haven't done so recently
            if (!hasRefreshed || DateTime.UtcNow - lastRefreshTime > this.refreshDelay)
            {
                return true;
            }

            // Identities can initially be created with no auth, and
            // have their auth type updated later. In this case we
            // must refresh the identity or we won't be able to auth
            // incoming OnBehalfOf connections for those identities.
            Option<ServiceIdentity> identityOption = await this.GetServiceIdentity(id);
            return identityOption.Match(id => id.Authentication.Type == ServiceAuthenticationType.None, () => true);
        }

        void RefreshCache(object state)
        {
            lock (this.refreshCacheLock)
            {
                if (this.refreshCacheTask == null || this.refreshCacheTask.IsCompleted)
                {
                    Events.InitializingRefreshTask(this.periodicRefreshRate);
                    this.refreshCacheTask = this.RefreshCache();
                }
            }
        }

        async Task RefreshCache()
        {
            while (true)
            {
                try
                {
                    Events.StartingRefreshCycle();
                    var currentCacheIds = new List<string>();

                    IServiceIdentitiesIterator iterator = this.serviceProxy.GetServiceIdentitiesIterator();
                    while (iterator.HasNext)
                    {
                        IEnumerable<ServiceIdentity> batch = await iterator.GetNext();
                        foreach (ServiceIdentity serviceIdentity in batch)
                        {
                            try
                            {
                                await this.HandleNewServiceIdentity(serviceIdentity);
                                currentCacheIds.Add(serviceIdentity.Id);
                            }
                            catch (Exception e)
                            {
                                Events.ErrorProcessing(serviceIdentity, e);
                            }
                        }
                    }

                    // Diff and update
                    IList<string> allIds = await this.serviceIdentityHierarchy.GetAllIds();
                    IList<string> removedIds = allIds.Except(currentCacheIds).ToList();
                    await Task.WhenAll(removedIds.Select(id => this.HandleNoServiceIdentity(id)));
                }
                catch (Exception e)
                {
                    Events.ErrorInRefreshCycle(e);
                }

                Events.DoneRefreshCycle(this.periodicRefreshRate);
                this.refreshCacheCompleteSignal.Set();
                await this.IsReady();
            }
        }

        async Task IsReady()
        {
            Task refreshCacheSignalTask = this.refreshCacheSignal.WaitAsync();
            Task sleepTask = Task.Delay(this.periodicRefreshRate);
            Task task = await Task.WhenAny(refreshCacheSignalTask, sleepTask);
            if (task == refreshCacheSignalTask)
            {
                Events.RefreshSignalled();
            }
            else
            {
                Events.RefreshSleepCompleted();
            }
        }

        async Task<Option<ServiceIdentity>> GetServiceIdentityInternal(string id)
        {
            Preconditions.CheckNonWhiteSpace(id, nameof(id));
            return await this.serviceIdentityHierarchy.Get(id);
        }

        async Task HandleNoServiceIdentity(string id)
        {
            Option<ServiceIdentity> identity = await this.serviceIdentityHierarchy.Get(id);
            bool hasValidServiceIdentity = identity.Filter(s => s.Status == ServiceIdentityStatus.Enabled).HasValue;

            // Remove the target identity
            await this.serviceIdentityHierarchy.Remove(id);
            await this.encryptedStore.Remove(id);
            Events.NotInScope(id);

            if (hasValidServiceIdentity)
            {
                // Remove device if connected, if service identity existed and then was removed.
                this.ServiceIdentityRemoved?.Invoke(this, id);
            }
        }

        async Task HandleNewServiceIdentity(ServiceIdentity serviceIdentity)
        {
            Option<ServiceIdentity> existing = await this.serviceIdentityHierarchy.Get(serviceIdentity.Id);
            bool hasUpdated = existing.HasValue && !existing.Contains(serviceIdentity);

            await this.serviceIdentityHierarchy.InsertOrUpdate(serviceIdentity);
            await this.SaveServiceIdentityToStore(serviceIdentity.Id, new StoredServiceIdentity(serviceIdentity));
            Events.AddInScope(serviceIdentity.Id);

            if (hasUpdated)
            {
                this.ServiceIdentityUpdated?.Invoke(this, serviceIdentity);
            }
        }

        async Task SaveServiceIdentityToStore(string id, StoredServiceIdentity storedServiceIdentity)
        {
            string serviceIdentityString = JsonConvert.SerializeObject(storedServiceIdentity);
            await this.encryptedStore.Put(id, serviceIdentityString);
        }

        internal class StoredServiceIdentity
        {
            public StoredServiceIdentity(ServiceIdentity serviceIdentity)
                : this(Preconditions.CheckNotNull(serviceIdentity, nameof(serviceIdentity)).Id, serviceIdentity, DateTime.UtcNow)
            {
            }

            public StoredServiceIdentity(string id)
                : this(Preconditions.CheckNotNull(id, nameof(id)), null, DateTime.UtcNow)
            {
            }

            [JsonConstructor]
            StoredServiceIdentity(string id, ServiceIdentity serviceIdentity, DateTime timestamp)
            {
                this.ServiceIdentity = Option.Maybe(serviceIdentity);
                this.Id = Preconditions.CheckNotNull(id);
                this.Timestamp = timestamp;
            }

            [JsonProperty("serviceIdentity")]
            [JsonConverter(typeof(OptionConverter<ServiceIdentity>))]
            public Option<ServiceIdentity> ServiceIdentity { get; }

            [JsonProperty("id")]
            public string Id { get; }

            [JsonProperty("timestamp")]
            public DateTime Timestamp { get; }
        }

        static class Events
        {
            const int IdStart = HubCoreEventIds.DeviceScopeIdentitiesCache;
            static readonly ILogger Log = Logger.Factory.CreateLogger<IDeviceScopeIdentitiesCache>();

            enum EventIds
            {
                InitializingRefreshTask = IdStart,
                Created,
                ErrorInRefresh,
                StartingCycle,
                DoneCycle,
                ReceivedRequestToRefreshCache,
                SkipRefreshCache,
                RefreshSleepCompleted,
                RefreshSignalled,
                NotInScope,
                AddInScope,
                RefreshingServiceIdentity,
                SkipRefreshServiceIdentity,
                GettingServiceIdentity
            }

            public static void Created() =>
                Log.LogInformation((int)EventIds.Created, "Created device scope identities cache");

            public static void ErrorInRefreshCycle(Exception exception)
            {
                Log.LogWarning((int)EventIds.ErrorInRefresh, "Encountered an error while refreshing the device scope identities cache. Will retry the operation in some time...");
                Log.LogDebug((int)EventIds.ErrorInRefresh, exception, "Error details while refreshing the device scope identities cache");
            }

            public static void StartingRefreshCycle() =>
                Log.LogInformation((int)EventIds.StartingCycle, "Starting refresh of device scope identities cache");

            public static void DoneRefreshCycle(TimeSpan refreshRate) =>
                Log.LogDebug((int)EventIds.DoneCycle, $"Done refreshing device scope identities cache. Waiting for {refreshRate.TotalMinutes} minutes.");

            public static void ErrorRefreshingCache(Exception exception, string deviceId)
            {
                Log.LogWarning((int)EventIds.ErrorInRefresh, exception, $"Error while refreshing the service identity for {deviceId}");
            }

            public static void ErrorProcessing(ServiceIdentity serviceIdentity, Exception exception)
            {
                string id = serviceIdentity?.Id ?? "unknown";
                Log.LogWarning((int)EventIds.ErrorInRefresh, exception, $"Error while processing the service identity for {id}");
            }

            public static void ReceivedRequestToRefreshCache() =>
                Log.LogDebug((int)EventIds.ReceivedRequestToRefreshCache, "Received request to refresh cache.");

            internal static void SkipRefreshCache(DateTime lastRefreshTime, TimeSpan refreshDelay)
            {
                TimeSpan timeSinceLastRefresh = DateTime.UtcNow - lastRefreshTime;
                int secondsUntilNextRefresh = (int)(refreshDelay.TotalSeconds - timeSinceLastRefresh.TotalSeconds);
                Log.LogDebug((int)EventIds.SkipRefreshCache, $"Skipping cache refresh, waiting {secondsUntilNextRefresh} until refreshing again.");
            }

            public static void RefreshSignalled() =>
                Log.LogDebug((int)EventIds.RefreshSignalled, "Device scope identities refresh is ready because a refresh was signalled.");

            public static void RefreshSleepCompleted() =>
                Log.LogDebug((int)EventIds.RefreshSleepCompleted, "Device scope identities refresh is ready because the wait period is over.");

            public static void NotInScope(string id) =>
                Log.LogDebug((int)EventIds.NotInScope, $"{id} is not in device scope, removing from cache.");

            public static void AddInScope(string id) =>
                Log.LogDebug((int)EventIds.AddInScope, $"{id} is in device scope, adding to cache.");

            public static void GettingServiceIdentity(string id) =>
                Log.LogDebug((int)EventIds.GettingServiceIdentity, $"Getting service identity for {id}");

            public static void RefreshingServiceIdentity(string id) =>
                Log.LogDebug((int)EventIds.RefreshingServiceIdentity, $"Refreshing service identity for {id}");

            internal static void SkipRefreshServiceIdentity(string id, DateTime lastRefreshTime, TimeSpan refreshDelay)
            {
                TimeSpan timeSinceLastRefresh = DateTime.UtcNow - lastRefreshTime;
                int secondsUntilNextRefresh = (int)(refreshDelay.TotalSeconds - timeSinceLastRefresh.TotalSeconds);
                Log.LogDebug((int)EventIds.SkipRefreshServiceIdentity, $"Skipping refresh for identity: {id}, waiting {secondsUntilNextRefresh} until refreshing again.");
            }

            internal static void InitializingRefreshTask(TimeSpan refreshRate) =>
                Log.LogDebug((int)EventIds.InitializingRefreshTask, $"Initializing device scope identities cache refresh task to run every {refreshRate.TotalMinutes} minutes.");
        }
    }
}
