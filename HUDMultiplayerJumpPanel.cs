using Core.Dependency;
using Shapez2UILib;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Shapez2Multiplayer
{
    public class HUDMultiplayerJumpPanel : HUDPart
    {
        public static HUDMultiplayerJumpPanel? Instance { get; private set; }

        private readonly Dictionary<uint, HUDPlayerJumpEntry> PlayerEntries = new Dictionary<uint, HUDPlayerJumpEntry>();
        private HUDPlayerJumpEntry? HostEntry;

        [Construct]
        private void Construct()
        {
            Instance = this;
            if (MultiplayerCore.Hosting && MultiplayerCore.socketManager != null)
            {
                foreach (var connection in MultiplayerCore.socketManager.Connected)
                {
                    AddPlayer(connection);
                }
            }
            else if (MultiplayerCore.Client && MultiplayerCore.connectionManager != null)
            {
                AddHost(MultiplayerCore.connectionManager.ConnectionManager.Connection);
                foreach (var connection in MultiplayerCore.connectionManager.Connections)
                {
                    AddPlayer(connection);
                }
            }
        }

        public void AddHost(IConnection connection)
        {
            if (HostEntry != null) return;
            HostEntry = CreateEntry(connection, true);
        }

        public void AddPlayer(IConnection connection)
        {
            if (MultiplayerCore.Client && MultiplayerCore.connectionManager != null &&
                connection.UniversalId == MultiplayerCore.connectionManager.UniversalId)
            {
                return;
            }
            if (PlayerEntries.ContainsKey(connection.UniversalId)) return;
            PlayerEntries.Add(connection.UniversalId, CreateEntry(connection, false));
        }

        private HUDPlayerJumpEntry CreateEntry(IConnection connection, bool representsHost)
        {
            GameObject entryObject = new GameObject(
                representsHost ? "JumpToHost" : "JumpTo" + connection.UniversalId,
                typeof(RectTransform),
                typeof(LayoutElement));
            entryObject.transform.SetParent(transform, false);
            entryObject.layer = LayerMask.NameToLayer("UI");

            var layoutElement = entryObject.GetComponent<LayoutElement>();
            layoutElement.minWidth = 300;
            layoutElement.preferredWidth = 300;
            layoutElement.minHeight = 58;
            layoutElement.preferredHeight = 58;

            var entry = entryObject.AddComponent<HUDPlayerJumpEntry>();
            entry.Connection = connection;
            entry.RepresentsHost = representsHost;
            UIFactory.AddPanel(entryObject.transform, entry);

            var nameText = UIFactory.AddTextPrimary(entryObject.transform);
            nameText.gameObject.name = "PlayerName";
            nameText.fontStyle = FontStyles.Normal;
            var nameRect = nameText.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = new Vector2(0.62f, 1f);
            nameRect.offsetMin = new Vector2(16f, 8f);
            nameRect.offsetMax = new Vector2(-8f, -8f);
            entry.NameText = nameText;

            var jumpButton = UIFactory.AddButton(entryObject.transform, entry, secondary: true);
            jumpButton.gameObject.name = "JumpButton";
            var jumpRect = jumpButton.GetComponent<RectTransform>();
            jumpRect.anchorMin = new Vector2(0.62f, 0f);
            jumpRect.anchorMax = Vector2.one;
            jumpRect.offsetMin = new Vector2(8f, 8f);
            jumpRect.offsetMax = new Vector2(-8f, -8f);
            entry.JumpButton = jumpButton;

            this.GetDependencyResolver().Inject(entry);
            return entry;
        }

        public void RemovePlayer(IConnection connection)
        {
            if (!PlayerEntries.TryGetValue(connection.UniversalId, out var entry)) return;
            PlayerEntries.Remove(connection.UniversalId);
            entry.Dispose();
            GameObject.Destroy(entry.gameObject);
        }

        public void ClearPlayers()
        {
            foreach (var entry in PlayerEntries.Values.ToList())
            {
                entry.Dispose();
                GameObject.Destroy(entry.gameObject);
            }
            PlayerEntries.Clear();
            if (HostEntry != null)
            {
                HostEntry.Dispose();
                GameObject.Destroy(HostEntry.gameObject);
                HostEntry = null;
            }
        }

        public override void OnUpdate(InputDownstreamContext context)
        {
            HostEntry?.EntryUpdate();
            foreach (var entry in PlayerEntries.Values)
            {
                entry.EntryUpdate();
            }
        }

        public override void OnDispose()
        {
            ClearPlayers();
            if (Instance == this) Instance = null;
        }
    }
}
