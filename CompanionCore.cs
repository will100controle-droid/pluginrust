using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("CompanionCore", "CompanionTeam", "1.0.0")]
    [Description("Sistema de NPC companheiro pessoal com módulo Farmer integrado")]
    public class CompanionCore : RustPlugin
    {
        #region Referências a Plugins Externos

        [PluginReference] Plugin Friends;
        [PluginReference] Plugin Clans;
        [PluginReference] Plugin ZoneManager;
        [PluginReference] Plugin ImageLibrary;

        #endregion

        #region Constantes

        private const string PERM_USE        = "companioncore.use";
        private const string PERM_NOCOOLDOWN = "companioncore.nocooldown";
        private const string NPC_PREFAB      = "assets/rust.ai/agents/npcplayer/pet/frankensteinpet.prefab";
        private const string UI_PANEL        = "CompanionCore_UI";
        private const string UI_HP           = "CompanionCore_HP";

        #endregion

        #region Configuração

        private PluginConfig _cfg;

        public class StartKitConfig
        {
            public List<string> wear  = new List<string> { "shoes.boots", "pants", "hoodie", "mask.bandana", "hat.boonie" };
            public List<string> belt  = new List<string> { "salvaged.sword" };
            public List<string> main  = new List<string> { "largemedkit", "largemedkit", "bandage", "bandage", "bandage", "pickaxe", "hatchet" };
        }

        public class PluginConfig
        {
            public float  followDistance       = 3f;
            public float  collectRadius        = 15f;
            public float  lootRadius           = 10f;
            public float  autoHealThreshold    = 0.5f;
            public bool   farmerEnabled        = true;
            public float  farmerRadius         = 20f;
            public float  farmerReturnDistance = 25f;
            public float  botHealth            = 200f;
            public string botName              = "Companion of %PLAYER_NAME%";
            public StartKitConfig startKit     = new StartKitConfig();
            public List<string> healItems      = new List<string> { "largemedkit", "syringe.medical", "bandage" };
            public List<string> toolForTrees   = new List<string> { "hatchet", "chainsaw" };
            public List<string> toolForStones  = new List<string> { "pickaxe", "jackhammer" };
            public Dictionary<string, float> gatherRates = new Dictionary<string, float>
            {
                { "wood",       1f },
                { "stone",      1f },
                { "metal.ore",  1f },
                { "sulfur.ore", 1f }
            };
            public int  cooldownSeconds = 300;
            public bool allowPvP        = false;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<PluginConfig>();
                if (_cfg == null) LoadDefaultConfig();
            }
            catch { LoadDefaultConfig(); }
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => _cfg = new PluginConfig();
        protected override void SaveConfig()         => Config.WriteObject(_cfg);

        #endregion

        #region Dados Persistentes

        private StoredData _data;

        public class StoredData
        {
            public Dictionary<ulong, PlayerData> players = new Dictionary<ulong, PlayerData>();
        }

        public class PlayerData
        {
            public string lastMode    = "follow";
            public bool   lootEnabled = true;
            public long   cooldownEnd = 0;
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("CompanionCore");
                if (_data == null) _data = new StoredData();
            }
            catch { _data = new StoredData(); }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject("CompanionCore", _data);

        private PlayerData GetOrCreateData(ulong playerId)
        {
            if (!_data.players.ContainsKey(playerId))
                _data.players[playerId] = new PlayerData();
            return _data.players[playerId];
        }

        #endregion

        #region Controle de Bots

        // CompanionBot fica no owner (não no NPC) — sobrevive se o NPC for destruído pelo servidor
        private Dictionary<ulong, NPCPlayer> _activeBots = new Dictionary<ulong, NPCPlayer>();
        private Dictionary<ulong, ulong>     _botOwners  = new Dictionary<ulong, ulong>();

        private CompanionBot GetBot(BasePlayer player)        => player?.GetComponent<CompanionBot>();
        private CompanionBot GetBotByOwner(ulong ownerId)
        {
            var owner = BasePlayer.FindByID(ownerId);
            return owner != null ? GetBot(owner) : null;
        }

        #endregion

        #region Inicialização

        private void Init()
        {
            permission.RegisterPermission(PERM_USE, this);
            permission.RegisterPermission(PERM_NOCOOLDOWN, this);
            LoadData();
            cmd.AddChatCommand("companion", this, "CmdCompanion");
            cmd.AddChatCommand("comp",      this, "CmdCompanion");
        }

        private void OnServerInitialized() => LoadConfig();

        private void Unload()
        {
            foreach (var pair in _activeBots.ToList())
            {
                var owner = BasePlayer.FindByID(pair.Key);
                var npc   = pair.Value;

                if (owner != null)
                {
                    CuiHelper.DestroyUi(owner, UI_PANEL);
                    CuiHelper.DestroyUi(owner, UI_HP);
                    var bot = GetBot(owner);
                    if (bot != null) UnityEngine.Object.Destroy(bot);
                }

                if (npc != null && !npc.IsDestroyed) npc.Kill();
            }

            _activeBots.Clear();
            _botOwners.Clear();
            SaveData();
        }

        #endregion

        #region Localização

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"]     = "Você não tem permissão para usar este comando.",
                ["BotAlreadyExists"] = "Você já tem um companheiro ativo.",
                ["BotSpawned"]       = "Seu companheiro foi chamado.",
                ["BotDismissed"]     = "Seu companheiro foi dispensado.",
                ["BotDied"]          = "Seu companheiro foi abatido. Aguarde {0} segundos para chamá-lo novamente.",
                ["Cooldown"]         = "Aguarde {0} segundos antes de chamar seu companheiro novamente.",
                ["ModeChanged"]      = "Modo alterado para: {0}",
                ["LootEnabled"]      = "Loot automático ativado.",
                ["LootDisabled"]     = "Loot automático desativado.",
                ["BotInventoryFull"] = "Inventário do companheiro cheio!",
                ["BotNeedHeals"]     = "Estou sem suprimentos médicos, me ajude!",
                ["BotStatus"]        = "Companheiro: {0} | Vida: {1}/{2} | Modo: {3}",
                ["NoBotActive"]      = "Você não tem um companheiro ativo.",
                ["HealForced"]       = "Companheiro usando item de cura.",
                ["NoHealItems"]      = "Companheiro sem itens de cura.",
                ["CommandList"]      = "Comandos: /companion spawn | dismiss | follow | stay | farmer | info | loot | heal"
            }, this, "pt-BR");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"]     = "You do not have permission to use this command.",
                ["BotAlreadyExists"] = "You already have an active companion.",
                ["BotSpawned"]       = "Your companion has been summoned.",
                ["BotDismissed"]     = "Your companion has been dismissed.",
                ["BotDied"]          = "Your companion was defeated. Wait {0} seconds to summon again.",
                ["Cooldown"]         = "Wait {0} seconds before summoning your companion again.",
                ["ModeChanged"]      = "Mode changed to: {0}",
                ["LootEnabled"]      = "Auto loot enabled.",
                ["LootDisabled"]     = "Auto loot disabled.",
                ["BotInventoryFull"] = "Companion inventory is full!",
                ["BotNeedHeals"]     = "I'm out of medical supplies, help me!",
                ["BotStatus"]        = "Companion: {0} | Health: {1}/{2} | Mode: {3}",
                ["NoBotActive"]      = "You don't have an active companion.",
                ["HealForced"]       = "Companion is using a heal item.",
                ["NoHealItems"]      = "Companion has no heal items.",
                ["CommandList"]      = "Commands: /companion spawn | dismiss | follow | stay | farmer | info | loot | heal"
            }, this, "en");
        }

        private string Lang(string key, string userId, params object[] args)
            => string.Format(lang.GetMessage(key, this, userId), args);

        #endregion

        #region Comandos de Chat

        private void CmdCompanion(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE))
            {
                SendMessage(player, Lang("NoPermission", player.UserIDString));
                return;
            }

            if (args == null || args.Length == 0)
            {
                SendMessage(player, Lang("CommandList", player.UserIDString));
                return;
            }

            switch (args[0].ToLower())
            {
                case "spawn":   CmdSpawn(player);             break;
                case "dismiss": CmdDismiss(player);           break;
                case "follow":  CmdSetMode(player, "follow"); break;
                case "stay":    CmdSetMode(player, "stay");   break;
                case "farmer":  CmdSetMode(player, "farmer"); break;
                case "info":    CmdInfo(player);              break;
                case "loot":    CmdToggleLoot(player);        break;
                case "heal":    CmdHeal(player);              break;
                default:        SendMessage(player, Lang("CommandList", player.UserIDString)); break;
            }
        }

        private void CmdSpawn(BasePlayer player)
        {
            if (_activeBots.ContainsKey(player.userID))
            {
                SendMessage(player, Lang("BotAlreadyExists", player.UserIDString));
                return;
            }

            var data = GetOrCreateData(player.userID);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (!permission.UserHasPermission(player.UserIDString, PERM_NOCOOLDOWN) && data.cooldownEnd > now)
            {
                SendMessage(player, Lang("Cooldown", player.UserIDString, data.cooldownEnd - now));
                return;
            }

            SpawnBot(player);
            SendMessage(player, Lang("BotSpawned", player.UserIDString));
        }

        private void CmdDismiss(BasePlayer player)
        {
            if (!_activeBots.ContainsKey(player.userID))
            {
                SendMessage(player, Lang("NoBotActive", player.UserIDString));
                return;
            }
            DismissBot(player);
            SendMessage(player, Lang("BotDismissed", player.UserIDString));
        }

        private void CmdSetMode(BasePlayer player, string mode)
        {
            var bot = GetBot(player);
            if (bot == null)
            {
                SendMessage(player, Lang("NoBotActive", player.UserIDString));
                return;
            }

            bot.SetMode(mode);

            var data = GetOrCreateData(player.userID);
            data.lastMode = mode;
            SaveData();

            RefreshUIBase(player, bot.Npc);
            SendMessage(player, Lang("ModeChanged", player.UserIDString, mode));
        }

        private void CmdInfo(BasePlayer player)
        {
            var bot = GetBot(player);
            if (bot == null)
            {
                SendMessage(player, Lang("NoBotActive", player.UserIDString));
                return;
            }

            var npc = bot.Npc;
            SendMessage(player, Lang("BotStatus", player.UserIDString,
                npc.displayName,
                Mathf.RoundToInt(npc.health),
                Mathf.RoundToInt(npc.MaxHealth()),
                bot.CurrentMode));
        }

        private void CmdToggleLoot(BasePlayer player)
        {
            var bot = GetBot(player);
            if (bot == null)
            {
                SendMessage(player, Lang("NoBotActive", player.UserIDString));
                return;
            }

            bot.LootEnabled = !bot.LootEnabled;

            var data = GetOrCreateData(player.userID);
            data.lootEnabled = bot.LootEnabled;
            SaveData();

            RefreshUIBase(player, bot.Npc);
            SendMessage(player, bot.LootEnabled
                ? Lang("LootEnabled",  player.UserIDString)
                : Lang("LootDisabled", player.UserIDString));
        }

        private void CmdHeal(BasePlayer player)
        {
            var bot = GetBot(player);
            if (bot == null)
            {
                SendMessage(player, Lang("NoBotActive", player.UserIDString));
                return;
            }

            SendMessage(player, bot.ForceHeal()
                ? Lang("HealForced",  player.UserIDString)
                : Lang("NoHealItems", player.UserIDString));
        }

        #endregion

        #region Spawn e Dismiss do Bot

        private void SpawnBot(BasePlayer player)
        {
            Vector3 spawnPos = player.transform.position + player.transform.forward * 2f + Vector3.up * 0.1f;

            var entity = GameManager.server.CreateEntity(NPC_PREFAB, spawnPos, Quaternion.identity);
            if (entity == null) return;

            var npc = entity as NPCPlayer;
            if (npc == null) { entity.Kill(); return; }

            npc.OwnerID     = player.userID;
            npc.displayName = _cfg.botName.Replace("%PLAYER_NAME%", player.displayName);
            npc.startHealth = _cfg.botHealth;
            npc.Spawn();
            npc.InitializeHealth(_cfg.botHealth, _cfg.botHealth);

            var frankenstein = npc.GetComponent<FrankensteinPet>();
            frankenstein?.ApplyPetStatModifiers();

            GiveStartKit(npc);

            // Controller fica no owner — mais robusto, sobrevive se o NPC for destruído pelo engine
            var data = GetOrCreateData(player.userID);
            var bot  = player.gameObject.AddComponent<CompanionBot>();
            bot.Initialize(npc, player, this, _cfg, data.lootEnabled, data.lastMode);

            _activeBots[player.userID]   = npc;
            _botOwners[npc.net.ID.Value] = player.userID;

            DrawUI(player, npc);
            Interface.CallHook("OnCompanionSpawned", player, npc);
        }

        private void GiveStartKit(BasePlayer npc)
        {
            if (npc == null || npc.IsDestroyed) return;
            foreach (var s in _cfg.startKit.wear)  CreateItem(s)?.MoveToContainer(npc.inventory.containerWear);
            foreach (var s in _cfg.startKit.belt)  CreateItem(s)?.MoveToContainer(npc.inventory.containerBelt);
            foreach (var s in _cfg.startKit.main)  CreateItem(s)?.MoveToContainer(npc.inventory.containerMain);
        }

        private Item CreateItem(string shortname)
        {
            var def = ItemManager.FindItemDefinition(shortname);
            return def == null ? null : ItemManager.CreateByItemID(def.itemid, 1);
        }

        private void DismissBot(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_PANEL);
            CuiHelper.DestroyUi(player, UI_HP);

            var bot = GetBot(player);
            if (bot != null) UnityEngine.Object.Destroy(bot);

            if (!_activeBots.TryGetValue(player.userID, out NPCPlayer npc)) return;
            _activeBots.Remove(player.userID);

            if (npc != null && !npc.IsDestroyed)
            {
                _botOwners.Remove(npc.net.ID.Value);
                npc.Kill();
            }
        }

        #endregion

        #region Hooks do Oxide

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity?.net == null) return;

            ulong netId = entity.net.ID.Value;
            if (!_botOwners.TryGetValue(netId, out ulong ownerId)) return;

            _botOwners.Remove(netId);
            _activeBots.Remove(ownerId);

            var owner = BasePlayer.FindByID(ownerId);

            if (owner != null)
            {
                CuiHelper.DestroyUi(owner, UI_PANEL);
                CuiHelper.DestroyUi(owner, UI_HP);

                // Destrói o controller que está no owner
                var bot = GetBot(owner);
                if (bot != null) UnityEngine.Object.Destroy(bot);
            }

            var data     = GetOrCreateData(ownerId);
            data.cooldownEnd = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _cfg.cooldownSeconds;
            SaveData();

            if (owner != null)
                SendMessage(owner, Lang("BotDied", owner.UserIDString, _cfg.cooldownSeconds));

            Interface.CallHook("OnCompanionDied", owner, entity);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            if (_activeBots.ContainsKey(player.userID))
                DismissBot(player);
        }

        #endregion

        #region Interface Visual (CUI)

        // UI_PANEL: AnchorMin="0.72 0.82" AnchorMax="0.99 0.99"
        // UI_HP: painel independente no Hud — refresh isolado a cada 1s
        // HP slot absoluto calculado das âncoras do panel: AnchorMin="0.7254 0.9169" AnchorMax="0.9846 0.9373"

        public void DrawUI(BasePlayer player, NPCPlayer npc)
        {
            if (player == null || npc == null || npc.IsDestroyed) return;
            DrawUIBase(player, npc);
            DrawUIHp(player, npc);
        }

        public void DrawUIBase(BasePlayer player, NPCPlayer npc)
        {
            if (player == null || npc == null || npc.IsDestroyed) return;
            CuiHelper.DestroyUi(player, UI_PANEL);

            var bot      = GetBot(player);
            string mode  = bot != null ? bot.CurrentMode.ToString().ToLower() : "follow";
            bool lootOn  = bot?.LootEnabled ?? true;

            string followColor = mode == "follow" ? "0.2 0.5 0.8 0.95" : "0.25 0.25 0.25 0.9";
            string stayColor   = mode == "stay"   ? "0.2 0.5 0.8 0.95" : "0.25 0.25 0.25 0.9";
            string farmerColor = mode == "farmer" ? "0.2 0.5 0.8 0.95" : "0.25 0.25 0.25 0.9";
            string lootColor   = lootOn ? "0.1 0.6 0.1 0.9" : "0.5 0.1 0.1 0.9";
            string lootText    = lootOn ? "LOOT ✓" : "LOOT ✗";

            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image         = { Color = "0.1 0.1 0.1 0.85" },
                RectTransform = { AnchorMin = "0.72 0.82", AnchorMax = "0.99 0.99" },
                CursorEnabled = false
            }, "Hud", UI_PANEL);

            ui.Add(new CuiLabel
            {
                Text          = { Text = "◆ COMPANION", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.02 0.82", AnchorMax = "0.98 0.98" }
            }, UI_PANEL);

            ui.Add(new CuiLabel
            {
                Text          = { Text = npc.displayName, FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.02 0.70", AnchorMax = "0.98 0.82" }
            }, UI_PANEL);

            ui.Add(new CuiButton
            {
                Button        = { Color = followColor, Command = "companion.ui mode follow" },
                Text          = { Text = "FOLLOW", FontSize = 9, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.02 0.43", AnchorMax = "0.32 0.56" }
            }, UI_PANEL);

            ui.Add(new CuiButton
            {
                Button        = { Color = stayColor, Command = "companion.ui mode stay" },
                Text          = { Text = "STAY", FontSize = 9, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.34 0.43", AnchorMax = "0.64 0.56" }
            }, UI_PANEL);

            ui.Add(new CuiButton
            {
                Button        = { Color = farmerColor, Command = "companion.ui mode farmer" },
                Text          = { Text = "FARMER", FontSize = 9, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.66 0.43", AnchorMax = "0.98 0.56" }
            }, UI_PANEL);

            ui.Add(new CuiButton
            {
                Button        = { Color = lootColor, Command = "companion.ui loot" },
                Text          = { Text = lootText, FontSize = 9, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.02 0.28", AnchorMax = "0.48 0.41" }
            }, UI_PANEL);

            ui.Add(new CuiButton
            {
                Button        = { Color = "0.8 0.1 0.1 0.9", Command = "companion.ui dismiss" },
                Text          = { Text = "DISMISS", FontSize = 9, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.50 0.28", AnchorMax = "0.98 0.41" }
            }, UI_PANEL);

            ui.Add(new CuiButton
            {
                Button        = { Color = "0.1 0.3 0.8 0.9", Command = "companion.ui heal" },
                Text          = { Text = "HEAL", FontSize = 9, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.98 0.26" }
            }, UI_PANEL);

            CuiHelper.AddUi(player, ui);
        }

        public void DrawUIHp(BasePlayer player, NPCPlayer npc)
        {
            if (player == null || npc == null || npc.IsDestroyed) return;
            CuiHelper.DestroyUi(player, UI_HP);

            float hp     = npc.health;
            float maxHp  = npc.MaxHealth();
            float hpFrac = maxHp > 0f ? Mathf.Clamp01(hp / maxHp) : 0f;

            string hpColor = hpFrac > 0.5f ? "0.1 0.8 0.1 0.9"
                           : hpFrac > 0.2f ? "0.9 0.8 0.1 0.9"
                                           : "0.9 0.1 0.1 0.9";

            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image         = { Color = "0.15 0.15 0.15 1" },
                RectTransform = { AnchorMin = "0.7254 0.9169", AnchorMax = "0.9846 0.9373" }
            }, "Hud", UI_HP);

            ui.Add(new CuiPanel
            {
                Image         = { Color = hpColor },
                RectTransform = { AnchorMin = "0 0", AnchorMax = $"{hpFrac:F3} 1" }
            }, UI_HP);

            ui.Add(new CuiLabel
            {
                Text          = { Text = $"{Mathf.RoundToInt(hp)}/{Mathf.RoundToInt(maxHp)}", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UI_HP);

            CuiHelper.AddUi(player, ui);
        }

        public void RefreshUIBase(BasePlayer player, NPCPlayer npc) => DrawUIBase(player, npc);
        public void RefreshUIHp(BasePlayer player, NPCPlayer npc)   => DrawUIHp(player, npc);

        #endregion

        #region Comandos de Console (CUI)

        [ConsoleCommand("companion.ui")]
        private void CmdCompanionUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!permission.UserHasPermission(player.UserIDString, PERM_USE)) return;

            switch (arg.GetString(0, "").ToLower())
            {
                case "mode":    CmdSetMode(player, arg.GetString(1, "follow")); break;
                case "loot":    CmdToggleLoot(player);                          break;
                case "dismiss": CmdDismiss(player);                             break;
                case "heal":    CmdHeal(player);                                break;
            }
        }

        #endregion

        #region API Pública

        [HookMethod("CompanionCore_HasBot")]
        public bool CompanionCore_HasBot(ulong playerId)
        {
            return _activeBots.TryGetValue(playerId, out NPCPlayer npc) && npc != null && !npc.IsDestroyed;
        }

        [HookMethod("CompanionCore_GetBot")]
        public NPCPlayer CompanionCore_GetBot(ulong playerId)
        {
            _activeBots.TryGetValue(playerId, out NPCPlayer npc);
            return npc != null && !npc.IsDestroyed ? npc : null;
        }

        [HookMethod("CompanionCore_SetMode")]
        public void CompanionCore_SetMode(ulong playerId, string mode)
        {
            var owner = BasePlayer.FindByID(playerId);
            if (owner == null) return;

            var bot = GetBot(owner);
            if (bot == null) return;

            bot.SetMode(mode);
            RefreshUIBase(owner, bot.Npc);
            Interface.CallHook("OnCompanionModeChanged", owner, bot.Npc, mode);
        }

        #endregion

        #region Auto-Update

        private const string UPDATE_URL = "https://raw.githubusercontent.com/will100controle-droid/pluginrust/main/CompanionCore.cs";

        [ConsoleCommand("companion.update")]
        private void CmdSelfUpdate(ConsoleSystem.Arg arg)
        {
            // Apenas console do servidor — não permite jogadores
            if (arg.Player() != null) return;

            Puts("CompanionCore: verificando atualização...");

            webrequest.Enqueue(UPDATE_URL, null, (code, body) =>
            {
                if (code != 200 || string.IsNullOrEmpty(body))
                {
                    Puts($"CompanionCore: falha ao baixar atualização (HTTP {code}).");
                    return;
                }

                string path = System.IO.Path.Combine(Interface.Oxide.PluginDirectory, "CompanionCore.cs");
                System.IO.File.WriteAllText(path, body);
                Puts("CompanionCore: arquivo atualizado. Recarregando...");
                Server.Command("oxide.reload CompanionCore");
            }, this);
        }

        #endregion

        #region Métodos Auxiliares

        public void SendMessage(BasePlayer player, string message)
        {
            if (player == null || !player.IsConnected) return;
            player.ChatMessage(message);
        }

        #endregion

        #region CompanionBot — Controlador de Comportamento

        // FacepunchBehaviour: integração nativa com o lifecycle do Rust (PersonalNPC pattern)
        // Fica no gameObject do OWNER — se o NPC for destruído pelo engine, o controller sobrevive
        public class CompanionBot : FacepunchBehaviour
        {
            public enum BotMode { Follow, Stay, Farmer, Combat }

            public  NPCPlayer     Npc           { get; private set; }
            private BasePlayer    _owner;
            private CompanionCore _plugin;
            private PluginConfig  _cfg;
            private BaseNavigator _navigator;

            public BotMode CurrentMode       { get; private set; }
            private BotMode _modeBeforeCombat;

            public bool LootEnabled { get; set; } = true;

            private BaseCombatEntity _target;
            private bool             _inCombat;
            private float            _lastAttackTime;

            private BaseEntity _farmTarget;
            private Vector3    _wanderTarget;

            public void Initialize(NPCPlayer npc, BasePlayer owner, CompanionCore plugin, PluginConfig cfg, bool lootEnabled, string lastMode)
            {
                Npc         = npc;
                _owner      = owner;
                _plugin     = plugin;
                _cfg        = cfg;
                LootEnabled = lootEnabled;

                // Brain fica LIGADO — ele cuida das animações e sync de rede
                // Pegamos o navigator através do brain para controlar o destino
                var frankenstein = npc.GetComponent<FrankensteinPet>();
                if (frankenstein?.Brain != null)
                    _navigator = frankenstein.Brain.Navigator;

                SetMode(lastMode);

                InvokeRepeating("Think",         0.5f, 0.5f);
                InvokeRepeating("CheckCombat",   1f,   1f);
                InvokeRepeating("CheckHeal",     5f,   5f);
                InvokeRepeating("CheckLoot",     2f,   2f);
                InvokeRepeating("RefreshUILoop", 1f,   1f);
            }

            public void SetMode(string modeStr)
            {
                _farmTarget   = null;
                _wanderTarget = Vector3.zero;
                StopMoving();

                switch (modeStr.ToLower())
                {
                    case "stay":   CurrentMode = BotMode.Stay;   break;
                    case "farmer": CurrentMode = BotMode.Farmer; break;
                    default:       CurrentMode = BotMode.Follow;  break;
                }
            }

            // ── Think ──────────────────────────────────────────────────────────

            private void Think()
            {
                if (Npc == null || Npc.IsDestroyed || _owner == null) return;
                if (_inCombat) return;

                switch (CurrentMode)
                {
                    case BotMode.Follow: ThinkFollow(); break;
                    case BotMode.Stay:   ThinkStay();   break;
                    case BotMode.Farmer: ThinkFarmer(); break;
                }
            }

            private void ThinkFollow()
            {
                float dist = Dist(Npc, _owner);

                if (dist > 60f)
                    TeleportToOwner();
                else if (dist > _cfg.followDistance)
                    MoveTo(_owner.transform.position, BaseNavigator.NavigationSpeed.Fast);
                else
                    StopMoving();
            }

            private void ThinkStay()
            {
                if (Dist(Npc, _owner) > 60f) CurrentMode = BotMode.Follow;
                else                         StopMoving();
            }

            private void ThinkFarmer()
            {
                float distToOwner = Dist(Npc, _owner);

                if (distToOwner > _cfg.farmerReturnDistance)
                {
                    if (distToOwner > 60f) TeleportToOwner();
                    else                   MoveTo(_owner.transform.position, BaseNavigator.NavigationSpeed.Fast);
                    return;
                }

                if (distToOwner < _cfg.followDistance + 2f)
                    TransferInventoryToOwner();

                if (_farmTarget == null || _farmTarget.IsDestroyed)
                    _farmTarget = FindFarmTarget();

                if (_farmTarget != null && !_farmTarget.IsDestroyed)
                {
                    if (Dist(Npc, _farmTarget) < 2f)
                    {
                        StopMoving();
                        CollectResource(_farmTarget);
                        _farmTarget = null;
                    }
                    else
                    {
                        MoveTo(_farmTarget.transform.position, BaseNavigator.NavigationSpeed.Normal);
                    }
                }
                else
                {
                    if (_wanderTarget == Vector3.zero || Dist(Npc.transform.position, _wanderTarget) < 1.5f)
                    {
                        Vector3 rand  = UnityEngine.Random.insideUnitSphere * _cfg.farmerRadius;
                        _wanderTarget = _owner.transform.position + new Vector3(rand.x, 0f, rand.z);
                    }
                    MoveTo(_wanderTarget, BaseNavigator.NavigationSpeed.Slow);
                }
            }

            // ── Combate ────────────────────────────────────────────────────────

            private void CheckCombat()
            {
                if (Npc == null || Npc.IsDestroyed) return;

                if (_inCombat)
                {
                    if (_target == null || _target.IsDestroyed || _target.IsDead())
                    {
                        _target     = null;
                        _inCombat   = false;
                        CurrentMode = _modeBeforeCombat;
                        return;
                    }

                    if (Dist(Npc, _target) > 2.5f) MoveTo(_target.transform.position, 6f);
                    else                            { StopMoving(); Attack(_target); }
                }
                else
                {
                    var enemy = FindEnemy();
                    if (enemy != null)
                    {
                        _target           = enemy;
                        _inCombat         = true;
                        _modeBeforeCombat = CurrentMode;
                    }
                }
            }

            private void Attack(BaseCombatEntity target)
            {
                if (Time.time - _lastAttackTime < 1f) return;
                if (Npc == null || target == null || target.IsDestroyed) return;

                _lastAttackTime = Time.time;

                float damage   = 25f;
                var activeItem = Npc.GetActiveItem();
                if (activeItem != null)
                {
                    var melee = activeItem.GetHeldEntity() as BaseMelee;
                    if (melee != null)
                    {
                        float total = melee.damageTypes.Sum(d => d.amount);
                        if (total > 0f) damage = total;
                    }
                }

                target.Hurt(damage, Rust.DamageType.Stab, Npc, false);
                RotateTo(target.transform.position);
            }

            private BaseCombatEntity FindEnemy()
            {
                if (Npc == null) return null;
                var entities = new List<BaseCombatEntity>();
                Vis.Entities(Npc.transform.position, 15f, entities);

                foreach (var ent in entities)
                {
                    if (ent == null || ent.IsDestroyed || ent.IsDead()) continue;
                    if (ent == Npc || ent == _owner) continue;

                    if (ent is BasePlayer bp)
                    {
                        if (bp.GetComponent<CompanionBot>() != null) continue;
                        if (!_cfg.allowPvP || bp == _owner) continue;
                        return ent;
                    }

                    if (ent is BaseNpc || ent is NPCPlayer) return ent;
                }

                return null;
            }

            // ── Cura ───────────────────────────────────────────────────────────

            private void CheckHeal()
            {
                if (Npc == null || Npc.IsDestroyed) return;
                if (Npc.MaxHealth() > 0f && Npc.health / Npc.MaxHealth() < _cfg.autoHealThreshold)
                    ForceHeal();
            }

            public bool ForceHeal()
            {
                if (Npc == null || Npc.IsDestroyed) return false;

                foreach (var shortname in _cfg.healItems)
                {
                    var found = Npc.inventory.containerMain.itemList.FirstOrDefault(i => i.info.shortname == shortname);
                    if (found == null) continue;

                    float amount = shortname == "largemedkit"     ? 100f
                                 : shortname == "syringe.medical" ? 75f
                                                                   : 15f;
                    Npc.Heal(amount);
                    found.UseItem(1);
                    return true;
                }

                if (_owner?.IsConnected == true)
                    _plugin.SendMessage(_owner, _plugin.Lang("BotNeedHeals", _owner.UserIDString));

                return false;
            }

            // ── Loot ───────────────────────────────────────────────────────────

            private void CheckLoot()
            {
                if (!LootEnabled || _inCombat || CurrentMode == BotMode.Stay) return;
                if (Npc == null || Npc.IsDestroyed) return;

                if (Npc.inventory.containerMain.IsFull())
                {
                    if (_owner?.IsConnected == true)
                        _plugin.SendMessage(_owner, _plugin.Lang("BotInventoryFull", _owner.UserIDString));
                    return;
                }

                var entities = new List<BaseEntity>();
                Vis.Entities(Npc.transform.position, _cfg.lootRadius, entities);

                foreach (var ent in entities)
                {
                    if (ent == null || ent.IsDestroyed) continue;

                    if (ent is LootContainer lc && IsLootableBarrel(lc)) { LootContainer(lc); continue; }
                    if (ent is NPCPlayerCorpse corpse)                   { LootCorpse(corpse); continue; }
                    if (ent is DroppedItem dropped && dropped.item != null)
                        dropped.item.MoveToContainer(Npc.inventory.containerMain);
                }
            }

            private bool IsLootableBarrel(LootContainer cont)
            {
                string name = cont?.PrefabName ?? "";
                if (name.Contains("crate_elite") || name.Contains("crate_normal")) return false;
                return name.Contains("barrel") || name.Contains("trash");
            }

            private void LootContainer(LootContainer cont)
            {
                if (cont == null || cont.IsDestroyed || cont.inventory == null) return;
                foreach (var item in cont.inventory.itemList.ToList())
                {
                    if (Npc.inventory.containerMain.IsFull()) break;
                    item?.MoveToContainer(Npc.inventory.containerMain);
                }
                if (cont.inventory.itemList.Count == 0) cont.Kill();
            }

            private void LootCorpse(NPCPlayerCorpse corpse)
            {
                if (corpse == null || corpse.IsDestroyed) return;
                foreach (var container in corpse.containers)
                {
                    if (container == null) continue;
                    foreach (var item in container.itemList.ToList())
                    {
                        if (Npc.inventory.containerMain.IsFull()) break;
                        item?.MoveToContainer(Npc.inventory.containerMain);
                    }
                }
            }

            // ── Farmer ─────────────────────────────────────────────────────────

            private BaseEntity FindFarmTarget()
            {
                if (Npc == null) return null;
                var entities = new List<BaseEntity>();
                Vis.Entities(Npc.transform.position, _cfg.collectRadius, entities);

                foreach (var ent in entities)
                {
                    if (ent == null || ent.IsDestroyed) continue;
                    if (ent is TreeEntity || ent is OreResourceEntity) return ent;
                    if (ent is LootContainer lc && IsLootableBarrel(lc)) return lc;
                }
                return null;
            }

            private void CollectResource(BaseEntity target)
            {
                if (target == null || target.IsDestroyed || Npc == null || Npc.IsDestroyed) return;

                if (target is TreeEntity || target is OreResourceEntity)
                {
                    var dispenser = target.GetComponent<ResourceDispenser>();
                    if (dispenser != null)
                    {
                        // DoGather via HitInfo — aciona hooks do jogo, respeita outros plugins de rate (PersonalNPC pattern)
                        var hitInfo = new HitInfo { Initiator = Npc, DoHitEffects = false };
                        dispenser.DoGather(hitInfo);

                        // Aplica nossas taxas configuradas em cima do que foi dado
                        foreach (var res in dispenser.containedItems.ToList())
                        {
                            if (res?.itemDef == null) continue;
                            string name = res.itemDef.shortname;
                            float  rate = _cfg.gatherRates.ContainsKey(name) ? _cfg.gatherRates[name] : 1f;
                            if (rate <= 1f) continue;

                            // Só dá o extra acima de 1x (o 1x já veio do DoGather)
                            int bonus = Mathf.Max(0, Mathf.RoundToInt(res.amount * (rate - 1f)));
                            if (bonus > 0)
                                ItemManager.CreateByItemID(res.itemDef.itemid, bonus)?.MoveToContainer(Npc.inventory.containerMain);
                        }

                        // Bonus final do ResourceDispenser (API correta do Rust)
                        dispenser.AssignFinishBonus(Npc, 0.5f, null);
                    }

                    target.Kill();
                }
                else if (target is LootContainer lc)
                {
                    LootContainer(lc);
                }
            }

            private void TransferInventoryToOwner()
            {
                if (Npc == null || _owner == null) return;

                var keep = new HashSet<string>(_cfg.healItems.Concat(_cfg.toolForTrees).Concat(_cfg.toolForStones));

                foreach (var item in Npc.inventory.containerMain.itemList.ToList())
                {
                    if (item == null || keep.Contains(item.info.shortname)) continue;

                    if (!_owner.inventory.containerMain.IsFull())
                        item.MoveToContainer(_owner.inventory.containerMain);
                    else
                        item.Drop(_owner.transform.position + Vector3.up * 0.5f, Vector3.zero);
                }
            }

            // ── Movimentação ───────────────────────────────────────────────────

            private void MoveTo(Vector3 pos, BaseNavigator.NavigationSpeed speed)
            {
                if (Npc == null || _navigator == null) return;
                _navigator.SetDestination(GroundPos(pos), speed);
                _navigator.Resume();
            }

            private void StopMoving() => _navigator?.Pause();

            private void TeleportToOwner()
            {
                if (Npc == null || _owner == null) return;
                StopMoving();
                Vector3 pos = GroundPos(_owner.transform.position + _owner.transform.forward * 2f);
                Npc.transform.position = pos;
                Npc.SendNetworkUpdate();
            }

            private void RotateTo(Vector3 pos)
            {
                if (Npc == null) return;
                Vector3 dir = (pos - Npc.transform.position).normalized;
                dir.y = 0f;
                if (dir == Vector3.zero) return;
                Npc.ServerRotation = Quaternion.LookRotation(dir);
            }

            // Snap ao terreno: raycast primeiro, heightmap como fallback
            private static readonly int GroundMask = LayerMask.GetMask("Terrain", "World", "Construction");
            private Vector3 GroundPos(Vector3 pos)
            {
                RaycastHit hit;
                if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out hit, 100f, GroundMask))
                    return hit.point + Vector3.up * 0.05f;
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                return pos;
            }

            // ── UI Loop ────────────────────────────────────────────────────────

            private void RefreshUILoop()
            {
                if (Npc == null || Npc.IsDestroyed) return;
                if (_owner == null || !_owner.IsConnected) return;
                _plugin.RefreshUIHp(_owner, Npc);
            }

            // ── Helpers ────────────────────────────────────────────────────────

            private float Dist(BaseEntity a, BaseEntity b) => Vector3.Distance(a.transform.position, b.transform.position);
            private float Dist(Vector3 a,    Vector3 b)    => Vector3.Distance(a, b);

            private void OnDestroy()
            {
                try { _navigator?.Pause(); } catch { }
                CancelInvoke();
            }
        }

        #endregion
    }
}
