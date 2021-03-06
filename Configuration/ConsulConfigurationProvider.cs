﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Spiffy.Monitoring;

namespace ConsulRx.Configuration
{
    public class ConsulConfigurationProvider : ConfigurationProvider
    {
        private readonly IObservableConsul _consulClient;
        private readonly IEmergencyCache _emergencyCache;
        private readonly ConsulDependencies _dependencies;
        private readonly ServiceConfigMappingCollection _serviceConfigMappings;
        private readonly KVTreeConfigMappingCollection _kvTreeConfigMappings;
        private readonly KVItemConfigMappingCollection _kvItemConfigMappings;
        private readonly TimeSpan? _retryDelay;
        private ConsulState _consulState;

        public ConsulConfigurationProvider(IObservableConsul consulClient,
            IEmergencyCache emergencyCache, 
            ConsulDependencies dependencies,
            ServiceConfigMappingCollection serviceConfigMappings,
            KVTreeConfigMappingCollection kvTreeConfigMappings,
            KVItemConfigMappingCollection kvItemConfigMappings,
            TimeSpan? retryDelay = null)
        {
            _consulClient = consulClient;
            _emergencyCache = emergencyCache;
            _dependencies = dependencies;
            _serviceConfigMappings = serviceConfigMappings;
            _kvTreeConfigMappings = kvTreeConfigMappings;
            _kvItemConfigMappings = kvItemConfigMappings;
            _retryDelay = retryDelay;
        }

        public override void Load()
        {
            LoadAsync().GetAwaiter().GetResult();
        }

        public async Task LoadAsync()
        {
            var eventContext = new EventContext("ConsulRx.Configuration", "Load");
            try
            {
                _consulState = await _consulClient.ObserveDependencies(_dependencies).FirstAsync().ToTask();
                UpdateData();
                eventContext["LoadedFrom"] = "Consul";
            }
            catch (Exception exception)
            {
                eventContext.IncludeException(exception);
                if (_emergencyCache.TryLoad(out var cachedData))
                {
                    eventContext["LoadedFrom"] = "EmergencyCache";
                    Data = cachedData;
                }
                else
                {
                    eventContext["LoadedFrom"] = "UnableToLoad";
                    throw new ConsulRxConfigurationException("Unable to load configuration from consul. It is likely down or the endpoint is misconfigured. Please check the InnerException for details.", exception);
                }
            }
            finally
            {
                eventContext.Dispose();
            }

            if (_retryDelay.HasValue && _retryDelay != Timeout.InfiniteTimeSpan)
            {
                _consulClient.ObserveDependencies(_dependencies).DelayedRetry(_retryDelay.Value).Subscribe(updatedState =>
                {
                    using (var reloadEventContext = new EventContext("ConsulRx.Configuration", "Reload"))
                    {
                        try
                        {
                            _consulState = updatedState;
                            UpdateData();
                            OnReload();
                        }
                        catch (Exception ex)
                        {
                            reloadEventContext.IncludeException(ex);
                        }
                    }
                });
            }
        }

        private void UpdateData()
        {
            var data = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            AddServiceData(data);
            AddKVTreeData(data);
            AddKVItemData(data);

            Data = data;
            _emergencyCache.Save(data);
        }

        private void AddKVItemData(Dictionary<string, string> data)
        {
            foreach (var mapping in _kvItemConfigMappings)
            {
                var value = _consulState.KVStore.GetValue(mapping.ConsulKey);
                data[mapping.ConfigKey] = value;
            }
        }

        private void AddServiceData(Dictionary<string, string> data)
        {
            foreach (var mapping in _serviceConfigMappings)
            {
                var service = _consulState.GetService(mapping.ServiceName);
                if(service != null)
                {
                    mapping.BindToConfiguration(service, data);
                }
            }
        }

        private void AddKVTreeData(Dictionary<string, string> data)
        {
            foreach (var mapping in _kvTreeConfigMappings)
            {
                foreach (var kv in _consulState.KVStore.GetTree(mapping.ConsulKeyPrefix))
                {
                    var fullConfigKey = mapping.FullConfigKey(kv);
                    data[fullConfigKey] = kv.Value;
                }
            }
        }
    }
}