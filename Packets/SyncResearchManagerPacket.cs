using Core.Events;
using Core.Localization;
using Game.Core.Research;
using System.IO;
using System.Linq;

namespace Shapez2Multiplayer.Packets
{
    public class SyncResearchManagerPacket : IPacket
    {
        public ulong Revision;
        public ResearchManager.SerializedData ResearchManagerSerializedData;
        public SyncResearchManagerPacket() { }
        public SyncResearchManagerPacket(ResearchManager.SerializedData researchManagerSerializedData, ulong revision)
        {
            Revision = revision;
            ResearchManagerSerializedData = researchManagerSerializedData;
        }
        public SyncResearchManagerPacket(ResearchManager researchManager, ulong revision)
        {
            Revision = revision;
            ResearchManagerSerializedData = Encoding.SerializeResearchManager(researchManager);
        }

        public void Decode(Stream stream)
        {
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
            {
                Revision = reader.ReadUInt64();
            }
            ResearchManagerSerializedData = Encoding.DecodeResearchManagerSerializedData(stream);
        }

        public bool Encode(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(Revision);
            }
            Encoding.Encode(ResearchManagerSerializedData, stream);
            return true;
        }
        public void Handle(IConnection? connection, InfoConnection? routedFrom = null)
        {
            if (connection != null)
            {
                Shapez2Multiplayer.logger.Warning.Log("Tried to handle SyncResearchManagerPacket on server");
                return;
            }
            if (!MultiplayerSynchronization.ShouldApplyResearchRevision(Revision))
            {
                return;
            }

            var researchManager = Shapez2Multiplayer.Research;
            if (researchManager == null)
            {
                return;
            }

            MultiplayerSynchronization.ApplyingAuthoritativeResearchState = true;
            try
            {
                foreach (var id in ResearchManagerSerializedData.ResearchProgress.UnlockedUpgradeIds)
                {
                    var upgradeId = new Game.Core.Research.ResearchUpgradeId(id);
                    if (!researchManager.Progress.IsManuallyUnlocked(upgradeId))
                    {
                        var upgrade = researchManager.Layout.GetUpgrade(upgradeId);
                        researchManager.UnlockManager._OnPlayerAboutToUnlockResearch.Invoke(upgrade);
                        researchManager.UnlockManager.TryUnlock(upgrade, true);
                        researchManager.UnlockManager._OnResearchManuallyUnlockedByPlayer.Invoke(upgrade);
                    }
                }

                var allLinearUpgradeIds = researchManager.LinearUpgradeManager.Levels.Keys
                    .Select(id => id.Id)
                    .Concat(ResearchManagerSerializedData.LinearUpgrades.UpgradeLevels.Keys)
                    .Distinct()
                    .ToList();
                foreach (var id in allLinearUpgradeIds)
                {
                    var linearUpgradeId = new ResearchLinearUpgradeId(id);
                    ResearchManagerSerializedData.LinearUpgrades.UpgradeLevels.TryGetValue(id, out var targetLevel);
                    var hadCurrentLevel = researchManager.LinearUpgradeManager.Levels.TryGetValue(linearUpgradeId, out var currentLevel);
                    if (!hadCurrentLevel || currentLevel != targetLevel)
                    {
                        researchManager.LinearUpgradeManager.SetLevel(linearUpgradeId, targetLevel);

                        // SetLevel updates the model but does not emit the local
                        // player-action presentation used by the shop. Recreate it
                        // only for an authoritative increase, so every remote player
                        // gets the same upgrade animation and sound without spending
                        // points or applying the upgrade a second time.
                        var previousLevel = hadCurrentLevel ? currentLevel : 0;
                        if (targetLevel > previousLevel && researchManager.LinearUpgradeManager.TryGetUpgrade(linearUpgradeId, out var upgrade))
                        {
                            Shapez2Multiplayer.PassiveEventBus?.Emit<PlayerUpgradedLinearUpgradeEvent>(new PlayerUpgradedLinearUpgradeEvent(Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer, upgrade));
                            Shapez2Multiplayer.HudEvents?.ShowEpicNotification.Invoke(new HUDEpicNotificationData(
                                "research.research-linear-upgrade-improved-notification.title".T(),
                                "research.research-linear-upgrade-improved-notification.description".T()
                                    .Bind("name", upgrade.Title)
                                    .Bind("level", StringFormatting.FormatGenericCount(targetLevel + 1))));
                            Shapez2Multiplayer.GameSessionOrchestratorDependencyContainer.Resolve<IUISoundPlayer>().PlayResearchUnlocked();
                        }
                    }
                }

                var targetPlayerLevel = ResearchManagerSerializedData.PlayerLevel.Level;
                for (var level = researchManager.PlayerLevel.Level; level < targetPlayerLevel; level++)
                {
                    researchManager.PlayerLevel.GrantPlayerLevel();
                }
                if (researchManager.PlayerLevel.Level != targetPlayerLevel)
                {
                    // GrantPlayerLevel can reject a replay when the updated game
                    // considers the local certification UI state incomplete. The
                    // packet is authoritative, so reconcile the serialized model
                    // directly if the native progression path did not reach it.
                    if (!MultiplayerSynchronization.TryForcePlayerLevel(researchManager.PlayerLevel, targetPlayerLevel))
                    {
                        Shapez2Multiplayer.logger.Warning?.Log($"Could not reconcile operator level {researchManager.PlayerLevel.Level} to host level {targetPlayerLevel}.");
                    }
                }

                var levels = researchManager.PlayerLevelGoals.Levels;
                var goalLevelsChanged = false;
                foreach (var localGoalId in levels.Keys
                    .Where(id => !ResearchManagerSerializedData.PlayerLevelGoals.GoalLevels.ContainsKey(id.Id))
                    .ToList())
                {
                    levels.Remove(localGoalId);
                    goalLevelsChanged = true;
                }

                foreach (var kvp in ResearchManagerSerializedData.PlayerLevelGoals.GoalLevels)
                {
                    var levelGoalId = new PlayerLevelGoalId(kvp.Key);
                    var currentLevel = researchManager.PlayerLevelGoals.GetLevel(levelGoalId);
                    if (currentLevel == kvp.Value)
                    {
                        continue;
                    }

                    levels[levelGoalId] = kvp.Value;
                    goalLevelsChanged = true;
                    if (currentLevel < kvp.Value)
                    {
                        researchManager.PlayerLevelGoals._OnLeveledUp.Invoke(levelGoalId, kvp.Value);
                        Shapez2Multiplayer.GameSessionOrchestratorDependencyContainer.Resolve<IUISoundPlayer>().PlayResearchUnlocked();
                    }
                }

                if (goalLevelsChanged)
                {
                    researchManager.PlayerLevelGoals._OnChanged.Invoke();
                }

                // Progression events can grant currencies as a side effect. Apply
                // these totals last so the snapshot remains authoritative instead
                // of adding client-side rewards on top of the host balance.
                researchManager.BlueprintCurrencyManager.SetBlueprintCurrency(ResearchManagerSerializedData.BlueprintCurrency.BlueprintCurrency);
                researchManager.BlueprintCurrencyManager.TotalAmountSpent = ResearchManagerSerializedData.BlueprintCurrency.TotalAmountSpent;
                if (researchManager.PointStorage.Points.Amount != ResearchManagerSerializedData.PointCurrency.Points)
                {
                    researchManager.PointStorage.Set(new ResearchPointCurrency(ResearchManagerSerializedData.PointCurrency.Points));
                }
                researchManager.PointStorage.TotalSpent = new ResearchPointCurrency(ResearchManagerSerializedData.PointCurrency.TotalSpent);

                MultiplayerSynchronization.MarkResearchRevisionApplied(Revision);
                MultiplayerSynchronization.SetAuthoritativePlayerLevel(targetPlayerLevel);
            }
            catch (System.Exception ex)
            {
                Shapez2Multiplayer.logger.Warning?.Log($"Failed to apply authoritative research snapshot revision {Revision}.");
                Shapez2Multiplayer.logger.Warning?.LogException(ex);
            }
            finally
            {
                MultiplayerSynchronization.ApplyingAuthoritativeResearchState = false;
            }
        }
    }
}
