﻿using Microsoft.Extensions.Logging;
using ChatCore.Interfaces;
using ChatCore.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ChatCore.Config;
using System.Threading.Tasks;
using ChatCore.Services.Mixer;
using System.Threading;
using ChatCore.Models.OAuth;

namespace ChatCore.Services
{
    class OldStreamCoreConfig
    {
        public string TwitchChannelName;
        public string TwitchUsername;
        public string TwitchOAuthToken;
    }

    public class UserAuthProvider : IUserAuthProvider
    {
        public event Action<LoginCredentials> OnCredentialsUpdated;

        public LoginCredentials Credentials { get; } = new LoginCredentials();

        // If this is set, old StreamCore config data will be read in from this file.
        internal static string OldConfigPath = null;

        public UserAuthProvider(ILogger<UserAuthProvider> logger, IPathProvider pathProvider, MixerShortcodeAuthProvider mixerAuthProvider)
        {
            _logger = logger;
            _pathProvider = pathProvider;
            _mixerAuthProvider = mixerAuthProvider;
            _credentialsPath = Path.Combine(_pathProvider.GetDataPath(), "auth.ini");
            _credentialSerializer = new ObjectSerializer();
            _credentialSerializer.Load(Credentials, _credentialsPath);

            Task.Delay(1000).ContinueWith((task) =>
            {
                if (!string.IsNullOrEmpty(OldConfigPath) && File.Exists(OldConfigPath))
                {
                    _logger.LogInformation($"Trying to convert old StreamCore config at path {OldConfigPath}");
                    var old = new OldStreamCoreConfig();
                    _credentialSerializer.Load(old, OldConfigPath);
                    if (!string.IsNullOrEmpty(old.TwitchChannelName))
                    {
                        var oldName = old.TwitchChannelName.ToLower().Replace(" ", "");
                        if (!Credentials.Twitch_Channels.Contains(oldName))
                        {
                            Credentials.Twitch_Channels.Add(oldName);
                            _logger.LogInformation($"Added channel {oldName} from old StreamCore config.");
                        }
                    }
                    if (!string.IsNullOrEmpty(old.TwitchOAuthToken))
                    {
                        Credentials.Twitch_OAuthToken = old.TwitchOAuthToken;
                        _logger.LogInformation($"Pulled in old Twitch auth info from StreamCore config.");
                    }
                    var convertedPath = OldConfigPath + ".converted";
                    try
                    {
                        if (!File.Exists(convertedPath))
                        {
                            File.Move(OldConfigPath, convertedPath);
                        }
                        else
                        {
                            File.Delete(OldConfigPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "An exception occurred while trying to yeet old StreamCore config!");
                    }
                }
            });
        }

        private ILogger _logger;
        private IPathProvider _pathProvider;
        private MixerShortcodeAuthProvider _mixerAuthProvider;
        private string _credentialsPath;
        private ObjectSerializer _credentialSerializer;
        private DateTime _lastCredentialUpdateTime = DateTime.UtcNow;

        public void Save(bool callback = true)
        {
            _credentialSerializer.Save(Credentials, _credentialsPath);
            _lastCredentialUpdateTime = DateTime.UtcNow;
            if (callback)
            {
                OnCredentialsUpdated?.Invoke(Credentials);
            }
        }

        private CancellationTokenSource _cancellationToken;
        public async Task MixerLogin()
        {
            if(_cancellationToken != null) {
                _cancellationToken.Cancel();
            }
            _cancellationToken = new CancellationTokenSource();
            var grant = await _mixerAuthProvider.AwaitUserApproval(_cancellationToken.Token, launchBrowserProcess: true);
            if(grant != null)
            {
                Credentials.Mixer_AccessToken = grant.AccessToken;
                Credentials.Mixer_RefreshToken = grant.RefreshToken;
                Credentials.Mixer_ExpiresAt = grant.ExpiresAt;
                Save();
            }
        }



        SemaphoreSlim _refreshCredentialsLock = new SemaphoreSlim(1, 1);
        public async Task<bool> TryRefreshMixerCredentials()
        {
            var startTime = DateTime.UtcNow;
            await _refreshCredentialsLock.WaitAsync();
            if(startTime < _lastCredentialUpdateTime)
            {
                return true;
            }
            OAuthCredentials creds = null;
            try
            {
                creds = await _mixerAuthProvider.TryRefreshCredentials(Credentials.Mixer_RefreshToken);
                if (creds != null)
                {
                    Credentials.Mixer_RefreshToken = creds.RefreshToken;
                    Credentials.Mixer_AccessToken = creds.AccessToken;
                    Credentials.Mixer_ExpiresAt = creds.ExpiresAt;
                    Save(false);
                }
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "An unknown exception occurred while refreshing mixer credentials!");
            }
            finally
            {
                _refreshCredentialsLock.Release();
            }
            return creds != null;
        }
    }
}
