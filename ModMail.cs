/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using System;
using System.Collections.Generic;
using System.IO;

namespace Oxide.Plugins
{
    [Info("Mod Mail", "VisEntities", "1.0.0")]
    [Description(" ")]
    public class ModMail : RustPlugin
    {
        #region Fields

        private static ModMail _plugin;
        private static Configuration _config;
        private StoredData _storedData;

        private readonly Dictionary<StorageContainer, bool> _spawnedContainers = new Dictionary<StorageContainer, bool>();
        private Dictionary<ulong, DateTime> _lastMailTimes = new Dictionary<ulong, DateTime>();

        private const string PREFAB_STORAGE = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Send Mail Chat Command")]
            public string SendMailChatCommand { get; set; }
            
            [JsonProperty("Open Mail Archive Chat Command")]
            public string OpenMailArchiveChatCommand { get; set; }

            [JsonProperty("Maximum Archive Capacity")]
            public int MaximumArchiveCapacity { get; set; }

            [JsonProperty("Mail Cooldown Seconds")]
            public float MailCooldownSeconds { get; set; }

            [JsonProperty("Discord Webhook Url")]
            public string DiscordWebhookUrl { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                SendMailChatCommand = "sendmail",
                OpenMailArchiveChatCommand = "openmail",
                MaximumArchiveCapacity = 48,
                MailCooldownSeconds = 60f,
                DiscordWebhookUrl = ""
            };
        }

        #endregion Configuration

        #region Stored Data

        private class StoredData
        {
            [JsonProperty("Mails")]
            public List<MailData> Mails { get; set; } = new List<MailData>();
        }

        private class MailData
        {
            [JsonProperty("Sender Name")]
            public string SenderName;

            [JsonProperty("Sender Id")]
            public ulong SenderId;

            [JsonProperty("Timestamp")]
            public DateTime Timestamp;

            [JsonProperty("Content")]
            public string Content;
        }

        #endregion Stored Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            _storedData = DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());
            PermissionUtil.RegisterPermissions();
            cmd.AddChatCommand(_config.OpenMailArchiveChatCommand, this, nameof(cmdOpenMailArchive));
            cmd.AddChatCommand(_config.SendMailChatCommand, this, nameof(cmdSendMail));
        }

        private void Unload()
        {
            foreach (StorageContainer container in _spawnedContainers.Keys)
            {
                if (container != null)
                    container.Kill();
            }
            _spawnedContainers.Clear();

            _config = null;
            _plugin = null;
        }

        private void OnPlayerLootEnd(PlayerLoot inventory)
        {
            if (inventory == null || inventory.entitySource == null)
                return;

            StorageContainer container = inventory.entitySource as StorageContainer;
            if (container == null)
                return;

            if (!_spawnedContainers.TryGetValue(container, out bool shouldCaptureNote))
                return;

            if (shouldCaptureNote)
            {
                BasePlayer player = inventory.baseEntity;
                if (player == null)
                {
                    _spawnedContainers.Remove(container);
                    container.Kill();
                    return;
                }

                for (int i = 0; i < container.inventory.capacity; i++)
                {
                    Item item = container.inventory.GetSlot(i);
                    if (item != null && item.info.shortname == "note")
                    {
                        if (string.IsNullOrWhiteSpace(item.text))
                        {
                            MessagePlayer(player, Lang.EmptyNote);
                            break;
                        }
                        else
                        {
                            string noteContent = item.text.Trim();

                            if (_config.MaximumArchiveCapacity > 0 && _storedData.Mails.Count >= _config.MaximumArchiveCapacity)
                            {
                                _storedData.Mails.RemoveAt(0);
                            }

                            MailData mailData = new MailData();
                            mailData.SenderId = player.userID;
                            mailData.SenderName = player.displayName;
                            mailData.Content = noteContent;
                            mailData.Timestamp = DateTime.UtcNow;

                            _storedData.Mails.Add(mailData);
                            DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);

                            if (!string.IsNullOrEmpty(_config.DiscordWebhookUrl))
                            {
                                SendToDiscord(mailData);
                            }

                            MessagePlayer(player, Lang.MailSent);
                            NotifyOnlineAdmins(mailData);
                            break;
                        }
                    }
                }
            }

            _spawnedContainers.Remove(container);
            container.Kill();
        }

        #endregion Oxide Hooks

        #region Mailbox Creation and Stocking

        private StorageContainer CreateStorageContainer(bool shouldCaptureNote)
        {
            StorageContainer container = GameManager.server.CreateEntity(PREFAB_STORAGE) as StorageContainer;
            if (container == null)
                return null;

            container.enableSaving = false;
            container.limitNetworking = true;
            RemoveProblematicComponents(container);
            container.Spawn();

            if (shouldCaptureNote)
                container.inventory.capacity = 1;

            _spawnedContainers.Add(container, shouldCaptureNote);
            return container;
        }
 
        private void StockMailArchive(StorageContainer container)
        {
            foreach (MailData mailData in _storedData.Mails)
            {
                Item noteItem = ItemManager.CreateByName("note", 1);
                if (noteItem == null)
                    continue;

                string shortDate = mailData.Timestamp.ToLocalTime().ToString("g");
                noteItem.text = $"{mailData.Content}\n\n-- From {mailData.SenderName} ({mailData.SenderId}) on {shortDate} --";
                noteItem.name = $"From: {mailData.SenderName}";

                if (!noteItem.MoveToContainer(container.inventory))
                    noteItem.Remove();
            }
        }

        #endregion Mailbox Creation and Stocking

        #region Notifications

        private void NotifyOnlineAdmins(MailData mailData)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player != null && PermissionUtil.HasPermission(player, PermissionUtil.ADMIN))
                {
                    MessagePlayer(player, Lang.NewMailAlert, mailData.SenderName);
                }
            }
        }

        private void SendToDiscord(MailData mailData)
        {
            if (string.IsNullOrEmpty(_config.DiscordWebhookUrl))
                return;

            string shortDate = mailData.Timestamp.ToLocalTime().ToString("g");

            string messageTemplate = lang.GetMessage(Lang.DiscordMailAlert, this);
            string message = string.Format(messageTemplate, mailData.SenderName, mailData.SenderId, shortDate, mailData.Content);
            
            var postData = new
            {
                content = message
            };

            string json = JsonConvert.SerializeObject(postData);

            webrequest.Enqueue(
                _config.DiscordWebhookUrl,
                json,
                (code, response) =>
                {
                    if (code != 200 && code != 204)
                        PrintWarning($"Discord webhook returned code {code}. Response: {response}");
                },
                this,
                RequestMethod.POST,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            );
        }

        #endregion Notifications

        #region Permissions

        private static class PermissionUtil
        {
            public const string ADMIN = "modmail.admin";
            public const string USE = "modmail.use";
            private static readonly List<string> _permissions = new List<string>
            {
                USE,
                ADMIN
            };

            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Helper Functions

        public static void RemoveProblematicComponents(BaseEntity entity)
        {
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
        }

        #endregion Helper Functions

        #region Helper Classes

        public static class DataFileUtil
        {
            private const string FOLDER = "";

            public static string GetFilePath(string filename = null)
            {
                if (filename == null)
                    filename = _plugin.Name;

                return Path.Combine(FOLDER, filename);
            }

            public static string[] GetAllFilePaths()
            {
                string[] filePaths = Interface.Oxide.DataFileSystem.GetFiles(FOLDER);
                for (int i = 0; i < filePaths.Length; i++)
                {
                    filePaths[i] = filePaths[i].Substring(0, filePaths[i].Length - 5);
                }

                return filePaths;
            }

            public static bool Exists(string filePath)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filePath);
            }

            public static T Load<T>(string filePath) where T : class, new()
            {
                T data = Interface.Oxide.DataFileSystem.ReadObject<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static T LoadIfExists<T>(string filePath) where T : class, new()
            {
                if (Exists(filePath))
                    return Load<T>(filePath);
                else
                    return null;
            }

            public static T LoadOrCreate<T>(string filePath) where T : class, new()
            {
                T data = LoadIfExists<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static void Save<T>(string filePath, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject<T>(filePath, data);
            }

            public static void Delete(string filePath)
            {
                Interface.Oxide.DataFileSystem.DeleteDataFile(filePath);
            }
        }

        #endregion Helper Classes

        #region Commands

        private void cmdSendMail(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
            {
                MessagePlayer(player, Lang.NoPermission);
                return;
            }

            DateTime lastTime;
            if (_lastMailTimes.TryGetValue(player.userID, out lastTime))
            {
                double secondsSince = (DateTime.UtcNow - lastTime).TotalSeconds;
                if (secondsSince < _config.MailCooldownSeconds)
                {
                    MessagePlayer(player, Lang.MailCooldown);
                    return;
                }
            }

            _lastMailTimes[player.userID] = DateTime.UtcNow;

            StorageContainer container = CreateStorageContainer(shouldCaptureNote: true);
            if (container == null)
            {
                MessagePlayer(player, Lang.MailboxCreateFail);
                return;
            }

            timer.Once(1.5f, () =>
            {
                container.PlayerOpenLoot(player, doPositionChecks: false);
            });

            MessagePlayer(player, Lang.MailboxOpenPrompt);
        }

        private void cmdOpenMailArchive(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.ADMIN))
            {
                MessagePlayer(player, Lang.NoPermission);
                return;
            }

            StorageContainer container = CreateStorageContainer(shouldCaptureNote: false);
            if (container == null)
            {
                MessagePlayer(player, Lang.MailArchiveCreateFail);
                return;
            }

            StockMailArchive(container);

            timer.Once(0.5f, () =>
            {
                container.PlayerOpenLoot(player, doPositionChecks: false);
            });

            MessagePlayer(player, Lang.MailArchiveOpenPrompt);
        }

        #endregion Commands

        #region Localization

        private class Lang
        {
            public const string NoPermission = "NoPermission";
            public const string MailboxCreateFail = "MailboxCreateFail";
            public const string MailboxOpenPrompt = "MailboxOpenPrompt";
            public const string MailArchiveCreateFail = "MailArchiveCreateFail";
            public const string MailArchiveOpenPrompt = "MailArchiveOpenPrompt";
            public const string MailSent = "MailSent";
            public const string EmptyNote = "EmptyNote";
            public const string NewMailAlert = "NewMailAlert";
            public const string DiscordMailAlert = "DiscordMailAlert";
            public const string MailCooldown = "MailCooldown";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.NoPermission] = "You do not have permission to use this command.",
                [Lang.MailboxCreateFail] = "Failed to create mailbox!",
                [Lang.MailboxOpenPrompt] = "Place your note in the mailbox to send to admins.",
                [Lang.MailArchiveCreateFail] = "Failed to create the mail archive container!",
                [Lang.MailArchiveOpenPrompt] = "Opening the mail archive. Feel free to read or take any notes.",
                [Lang.MailSent] = "Thanks! Your note has been sent to the admins.",
                [Lang.EmptyNote] = "Your note is empty. Please type something before sending.",
                [Lang.NewMailAlert] = "New mail received from {0}. Use /openmail to view the archive.",
                [Lang.DiscordMailAlert] = "From: {0} ({1})\nTime: {2}\n```{3}```",
                [Lang.MailCooldown] = "Hold up! You cannot send another mail yet."

            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string message = _plugin.lang.GetMessage(messageKey, _plugin, player.UserIDString);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void MessagePlayer(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);
            _plugin.SendReply(player, message);
        }

        #endregion Localization
    }
}