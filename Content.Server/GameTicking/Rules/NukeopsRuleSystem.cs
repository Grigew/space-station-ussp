using System.Linq;
using Content.Server.Administration.Commands;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Ghost.Roles.Events;
using Content.Server.Humanoid;
using Content.Server.Humanoid.Systems;
using Content.Server.Mind;
using Content.Server.Mind.Components;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Server.Nuke;
using Content.Server.Preferences.Managers;
using Content.Server.Roles;
using Content.Server.RoundEnd;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Traitor;
using Content.Server.Traits.Assorted;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Dataset;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Nuke;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Server.Maps;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.GameTicking.Rules;

public sealed class NukeopsRuleSystem : GameRuleSystem<NukeopsRuleComponent>
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IServerPreferencesManager _prefs = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPlayerManager _playerSystem = default!;
    [Dependency] private readonly EmergencyShuttleSystem _emergency = default!;
    [Dependency] private readonly FactionSystem _faction = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoidSystem = default!;
    [Dependency] private readonly StationSpawningSystem _stationSpawningSystem = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly RoundEndSystem _roundEndSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly MapLoaderSystem _map = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly MindSystem _mindSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<RulePlayerSpawningEvent>(OnPlayersSpawning);
        SubscribeLocalEvent<NukeOperativeComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
        SubscribeLocalEvent<NukeExplodedEvent>(OnNukeExploded);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<NukeDisarmSuccessEvent>(OnNukeDisarm);
        SubscribeLocalEvent<NukeOperativeComponent, GhostRoleSpawnerUsedEvent>(OnPlayersGhostSpawning);
        SubscribeLocalEvent<NukeOperativeComponent, MindAddedMessage>(OnMindAdded);
        SubscribeLocalEvent<NukeOperativeComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<NukeOperativeComponent, ComponentRemove>(OnComponentRemove);
    }

    private void OnComponentInit(EntityUid uid, NukeOperativeComponent component, ComponentInit args)
    {
        var query = EntityQueryEnumerator<NukeopsRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var ruleEnt, out var nukeops, out var gameRule))
        {
            if (!GameTicker.IsGameRuleAdded(ruleEnt, gameRule))
                continue;

            // If entity has a prior mind attached, add them to the players list.
            if (!TryComp<MindContainerComponent>(uid, out var mindComponent))
                continue;

            var session = mindComponent.Mind?.Session;
            var name = MetaData(uid).EntityName;
            if (session != null)
                nukeops.OperativePlayers.Add(name, session);
            // Corvax-DionaPacifist-Start: Allow dionas nukes to harm
            RemComp<PacifistComponent>(uid);
            RemComp<PacifiedComponent>(uid);
            // Corvax-DionaPacifist-End
        }
    }

    private void OnComponentRemove(EntityUid uid, NukeOperativeComponent component, ComponentRemove args)
    {
        CheckRoundShouldEnd();
    }

    private void OnNukeExploded(NukeExplodedEvent ev)
    {
        var query = EntityQueryEnumerator<NukeopsRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var nukeops, out var gameRule))
        {
            if (!GameTicker.IsGameRuleAdded(uid, gameRule))
                continue;

            if (ev.OwningStation != null)
            {
                if (ev.OwningStation == nukeops.NukieOutpost)
                {
                    nukeops.WinConditions.Add(WinCondition.NukeExplodedOnNukieOutpost);
                    SetWinType(uid, WinType.CrewMajor, nukeops);
                    continue;
                }

                if (TryComp(nukeops.TargetStation, out StationDataComponent? data))
                {
                    var correctStation = false;
                    foreach (var grid in data.Grids)
                    {
                        if (grid != ev.OwningStation)
                        {
                            continue;
                        }

                        nukeops.WinConditions.Add(WinCondition.NukeExplodedOnCorrectStation);
                        SetWinType(uid, WinType.OpsMajor, nukeops);
                        correctStation = true;
                    }

                    if (correctStation)
                        continue;
                }

                nukeops.WinConditions.Add(WinCondition.NukeExplodedOnIncorrectLocation);
            }
            else
            {
                nukeops.WinConditions.Add(WinCondition.NukeExplodedOnIncorrectLocation);
            }

            _roundEndSystem.EndRound();
        }
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        var query = EntityQueryEnumerator<NukeopsRuleComponent>();
        while (query.MoveNext(out var uid, out var nukeops))
        {
            switch (ev.New)
            {
                case GameRunLevel.InRound:
                    OnRoundStart(uid, nukeops);
                    break;
                case GameRunLevel.PostRound:
                    OnRoundEnd(uid, nukeops);
                    break;
            }
        }
    }

    /// <summary>
    /// Loneops can only spawn if there is no nukeops active
    /// </summary>
    public bool CheckLoneOpsSpawn()
    {
        return !EntityQuery<NukeopsRuleComponent>().Any();
    }

    private void OnRoundStart(EntityUid uid, NukeopsRuleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        // TODO: This needs to try and target a Nanotrasen station. At the very least,
        // we can only currently guarantee that NT stations are the only station to
        // exist in the base game.

        var eligible = EntityQuery<StationEventEligibleComponent, FactionComponent>()
            .Where(x =>
                _faction.IsFactionHostile(component.Faction, x.Item2.Owner, x.Item2))
            .Select(x => x.Item1.Owner)
            .ToList();

        if (!eligible.Any())
            return;

        component.TargetStation = _random.Pick(eligible);

        var filter = Filter.Empty();
        var query = EntityQueryEnumerator<NukeOperativeComponent, ActorComponent>();
        while (query.MoveNext(out _, out _, out var actor))
        {
            _chatManager.DispatchServerMessage(actor.PlayerSession, Loc.GetString("nukeops-welcome", ("station", component.TargetStation.Value)));
            filter.AddPlayer(actor.PlayerSession);
        }
    }

    private void OnRoundEnd(EntityUid uid, NukeopsRuleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        // If the win condition was set to operative/crew major win, ignore.
        if (component.WinType == WinType.OpsMajor || component.WinType == WinType.CrewMajor)
            return;

        var nukeQuery = AllEntityQuery<NukeComponent, TransformComponent>();
        var centcomms = _emergency.GetCentcommMaps();

        while (nukeQuery.MoveNext(out var nuke, out var nukeTransform))
        {
            if (nuke.Status != NukeStatus.ARMED)
                continue;

            // UH OH
            if (centcomms.Contains(nukeTransform.MapID))
            {
                component.WinConditions.Add(WinCondition.NukeActiveAtCentCom);
                SetWinType(uid, WinType.OpsMajor, component);
                return;
            }

            if (nukeTransform.GridUid == null || component.TargetStation == null)
                continue;

            if (!TryComp(component.TargetStation.Value, out StationDataComponent? data))
                continue;

            foreach (var grid in data.Grids)
            {
                if (grid != nukeTransform.GridUid)
                    continue;

                component.WinConditions.Add(WinCondition.NukeActiveInStation);
                SetWinType(uid, WinType.OpsMajor, component);
                return;
            }
        }

        var allAlive = true;
        foreach (var (_, state) in EntityQuery<NukeOperativeComponent, MobStateComponent>())
        {
            if (state.CurrentState is MobState.Alive)
                continue;

            allAlive = false;
            break;
        }
        // If all nuke ops were alive at the end of the round,
        // the nuke ops win. This is to prevent people from
        // running away the moment nuke ops appear.
        if (allAlive)
        {
            SetWinType(uid, WinType.OpsMinor, component);
            component.WinConditions.Add(WinCondition.AllNukiesAlive);
            return;
        }

        component.WinConditions.Add(WinCondition.SomeNukiesAlive);

        var diskAtCentCom = false;
        var diskQuery = AllEntityQuery<NukeDiskComponent, TransformComponent>();

        while (diskQuery.MoveNext(out _, out var transform))
        {
            var diskMapId = transform.MapID;
            diskAtCentCom = centcomms.Contains(diskMapId);

            // TODO: The target station should be stored, and the nuke disk should store its original station.
            // This is fine for now, because we can assume a single station in base SS14.
            break;
        }

        // If the disk is currently at Central Command, the crew wins - just slightly.
        // This also implies that some nuclear operatives have died.
        if (diskAtCentCom)
        {
            SetWinType(uid, WinType.CrewMinor, component);
            component.WinConditions.Add(WinCondition.NukeDiskOnCentCom);
        }
        // Otherwise, the nuke ops win.
        else
        {
            SetWinType(uid, WinType.OpsMinor, component);
            component.WinConditions.Add(WinCondition.NukeDiskNotOnCentCom);
        }
    }

    private void OnRoundEndText(RoundEndTextAppendEvent ev)
    {
        foreach (var nukeops in EntityQuery<NukeopsRuleComponent>())
        {
            var winText = Loc.GetString($"nukeops-{nukeops.WinType.ToString().ToLower()}");

            ev.AddLine(winText);

            foreach (var cond in nukeops.WinConditions)
            {
                var text = Loc.GetString($"nukeops-cond-{cond.ToString().ToLower()}");

                ev.AddLine(text);
            }

            ev.AddLine(Loc.GetString("nukeops-list-start"));
            foreach (var (name, session) in nukeops.OperativePlayers)
            {
                var listing = Loc.GetString("nukeops-list-name", ("name", name), ("user", session.Name));
                ev.AddLine(listing);
            }
        }
    }

    private void SetWinType(EntityUid uid, WinType type, NukeopsRuleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        component.WinType = type;

        if (type == WinType.CrewMajor || type == WinType.OpsMajor)
            _roundEndSystem.EndRound();
    }

    private void CheckRoundShouldEnd()
    {
        var query = EntityQueryEnumerator<NukeopsRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var nukeops, out var gameRule))
        {
            if (!GameTicker.IsGameRuleAdded(uid, gameRule))
                continue;

            if (!nukeops.EndsRound || nukeops.WinType == WinType.CrewMajor || nukeops.WinType == WinType.OpsMajor)
                continue;

            // If there are any nuclear bombs that are active, immediately return. We're not over yet.
            var armed = false;
            foreach (var nuke in EntityQuery<NukeComponent>())
            {
                if (nuke.Status == NukeStatus.ARMED)
                {
                    armed = true;
                    break;
                }
            }
            if (armed)
                continue;

            MapId? shuttleMapId = Exists(nukeops.NukieShuttle)
                ? Transform(nukeops.NukieShuttle.Value).MapID
                : null;

            MapId? targetStationMap = null;
            if (nukeops.TargetStation != null && TryComp(nukeops.TargetStation, out StationDataComponent? data))
            {
                var grid = data.Grids.FirstOrNull();
                targetStationMap = grid != null
                    ? Transform(grid.Value).MapID
                    : null;
            }

            // Check if there are nuke operatives still alive on the same map as the shuttle,
            // or on the same map as the station.
            // If there are, the round can continue.
            var operatives = EntityQuery<NukeOperativeComponent, MobStateComponent, TransformComponent>(true);
            var operativesAlive = operatives
                .Where(ent =>
                    ent.Item3.MapID == shuttleMapId
                    || ent.Item3.MapID == targetStationMap)
                .Any(ent => ent.Item2.CurrentState == MobState.Alive && ent.Item1.Running);

            if (operativesAlive)
                continue; // There are living operatives than can access the shuttle, or are still on the station's map.

            // Check that there are spawns available and that they can access the shuttle.
            var spawnsAvailable = EntityQuery<NukeOperativeSpawnerComponent>(true).Any();
            if (spawnsAvailable && shuttleMapId == nukeops.NukiePlanet)
                continue; // Ghost spawns can still access the shuttle. Continue the round.

            // The shuttle is inaccessible to both living nuke operatives and yet to spawn nuke operatives,
            // and there are no nuclear operatives on the target station's map.
            nukeops.WinConditions.Add(spawnsAvailable
                ? WinCondition.NukiesAbandoned
                : WinCondition.AllNukiesDead);

            SetWinType(uid, WinType.CrewMajor, nukeops);
        }
    }

    private void OnNukeDisarm(NukeDisarmSuccessEvent ev)
    {
        CheckRoundShouldEnd();
    }

    private void OnMobStateChanged(EntityUid uid, NukeOperativeComponent component, MobStateChangedEvent ev)
    {
        if(ev.NewMobState == MobState.Dead)
            CheckRoundShouldEnd();
    }

    private void OnPlayersSpawning(RulePlayerSpawningEvent ev)
    {
        var query = EntityQueryEnumerator<NukeopsRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var nukeops, out var gameRule))
        {
            if (!GameTicker.IsGameRuleAdded(uid, gameRule))
                continue;

            if (!SpawnMap(uid, nukeops))
            {
                Logger.InfoS("nukies", "Failed to load map for nukeops");
                continue;
            }

            // Basically copied verbatim from traitor code
            var playersPerOperative = nukeops.PlayersPerOperative;
            var maxOperatives = nukeops.MaxOperatives;

            // Dear lord what is happening HERE.
            var everyone = new List<IPlayerSession>(ev.PlayerPool);
            var prefList = new List<IPlayerSession>();
            var medPrefList = new List<IPlayerSession>();
            var cmdrPrefList = new List<IPlayerSession>();
            var operatives = new List<IPlayerSession>();

            // The LINQ expression ReSharper keeps suggesting is completely unintelligible so I'm disabling it
            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
            foreach (var player in everyone)
            {
                if (!ev.Profiles.ContainsKey(player.UserId))
                {
                    continue;
                }

                var profile = ev.Profiles[player.UserId];
                if (profile.AntagPreferences.Contains(nukeops.OperativeRoleProto))
                {
                    prefList.Add(player);
                }
                if (profile.AntagPreferences.Contains(nukeops.MedicRoleProto))
	            {
	                medPrefList.Add(player);
	            }
                if (profile.AntagPreferences.Contains(nukeops.CommanderRolePrototype))
                {
                    cmdrPrefList.Add(player);
                }
            }

            var numNukies = MathHelper.Clamp(ev.PlayerPool.Count / playersPerOperative, 1, maxOperatives);

            for (var i = 0; i < numNukies; i++)
            {
                // TODO: Please fix this if you touch it.
                IPlayerSession nukeOp;
                // Only one commander, so we do it at the start
                if (i == 0)
                {
                    if (cmdrPrefList.Count == 0)
                    {
                        if (medPrefList.Count == 0)
                        {
                            if (prefList.Count == 0)
                            {
                                if (everyone.Count == 0)
                                {
                                    Logger.InfoS("preset", "Insufficient ready players to fill up with nukeops, stopping the selection");
                                    break;
                                }
                                nukeOp = _random.PickAndTake(everyone);
                                Logger.InfoS("preset", "Insufficient preferred nukeop commanders, agents or nukies, picking at random.");
                            }
                            else
                            {
                                nukeOp = _random.PickAndTake(prefList);
                                everyone.Remove(nukeOp);
                                Logger.InfoS("preset", "Insufficient preferred nukeop commander or agents, picking at random from regular op list.");
                            }
                        }
                        else
                        {
                            nukeOp = _random.PickAndTake(medPrefList);
                            everyone.Remove(nukeOp);
                            prefList.Remove(nukeOp);
                            Logger.InfoS("preset", "Insufficient preferred nukeop commanders, picking an agent");
                        }
                    }
                    else
                    {
                        nukeOp = _random.PickAndTake(cmdrPrefList);
                        everyone.Remove(nukeOp);
                        prefList.Remove(nukeOp);
                        medPrefList.Remove(nukeOp);
                        Logger.InfoS("preset", "Selected a preferred nukeop commander.");
                    }
                }
                else if (i == 1)
                {
                    if (medPrefList.Count == 0)
                    {
                        if (prefList.Count == 0)
                        {
                            if (everyone.Count == 0)
                            {
                                Logger.InfoS("preset", "Insufficient ready players to fill up with nukeops, stopping the selection");
                                break;
                            }
                            nukeOp = _random.PickAndTake(everyone);
                            Logger.InfoS("preset", "Insufficient preferred nukeop commanders, agents or nukies, picking at random.");
                        }
                        else
                        {
                            nukeOp = _random.PickAndTake(prefList);
                            everyone.Remove(nukeOp);
                            Logger.InfoS("preset", "Insufficient preferred nukeop commander or agents, picking at random from regular op list.");
                        }
                    }
                    else
                    {
                        nukeOp = _random.PickAndTake(medPrefList);
                        everyone.Remove(nukeOp);
                        Logger.InfoS("preset", "Insufficient preferred nukeop commanders, picking an agent");
                    }

                }
                else
                {
                    nukeOp = _random.PickAndTake(prefList);
                    everyone.Remove(nukeOp);
                    Logger.InfoS("preset", "Selected a preferred nukeop commander.");
                }

                operatives.Add(nukeOp);
            }

            SpawnOperatives(numNukies, operatives, false, nukeops);

            foreach (var session in operatives)
            {
                ev.PlayerPool.Remove(session);
                GameTicker.PlayerJoinGame(session);
                var name = session.AttachedEntity == null
                    ? string.Empty
                    : MetaData(session.AttachedEntity.Value).EntityName;
                // TODO: Fix this being able to have duplicates
                nukeops.OperativePlayers[name] = session;
            }
        }
    }

    private void OnPlayersGhostSpawning(EntityUid uid, NukeOperativeComponent component, GhostRoleSpawnerUsedEvent args)
    {
        var spawner = args.Spawner;

        if (!TryComp<NukeOperativeSpawnerComponent>(spawner, out var nukeOpSpawner))
            return;

        HumanoidCharacterProfile? profile = null;
        if (TryComp(args.Spawned, out ActorComponent? actor))
            profile = _prefs.GetPreferences(actor.PlayerSession.UserId).SelectedCharacter as HumanoidCharacterProfile;

        // todo: this is kinda awful for multi-nukies
        foreach (var nukeops in EntityQuery<NukeopsRuleComponent>())
        {
            SetupOperativeEntity(uid, nukeOpSpawner.OperativeName, nukeOpSpawner.OperativeStartingGear, profile, nukeops);

            nukeops.OperativeMindPendingData.Add(uid, nukeOpSpawner.OperativeRolePrototype);
        }
    }

    private void OnMindAdded(EntityUid uid, NukeOperativeComponent component, MindAddedMessage args)
    {
        if (!TryComp<MindContainerComponent>(uid, out var mindContainerComponent) || mindContainerComponent.Mind == null)
            return;

        var mind = mindContainerComponent.Mind;

        foreach (var nukeops in EntityQuery<NukeopsRuleComponent>())
        {
            if (nukeops.OperativeMindPendingData.TryGetValue(uid, out var role) || !nukeops.SpawnOutpost || !nukeops.EndsRound)
            {
                role ??= nukeops.OperativeRoleProto;
                _mindSystem.AddRole(mind, new NukeopsRole(mind, _prototypeManager.Index<AntagPrototype>(role)));
                nukeops.OperativeMindPendingData.Remove(uid);
            }

            if (!_mindSystem.TryGetSession(mind, out var playerSession))
                return;
            if (nukeops.OperativePlayers.ContainsValue(playerSession))
                return;

            var name = MetaData(uid).EntityName;

            nukeops.OperativePlayers.Add(name, playerSession);

            if (GameTicker.RunLevel != GameRunLevel.InRound)
                return;

            if (nukeops.TargetStation != null && !string.IsNullOrEmpty(Name(nukeops.TargetStation.Value)))
            {
                _chatManager.DispatchServerMessage(playerSession, Loc.GetString("nukeops-welcome", ("station", nukeops.TargetStation.Value)));

                 // Notificate player about new role assignment
                 _audioSystem.PlayGlobal(component.GreetSoundNotification, playerSession);
            }
        }
    }

    private bool SpawnMap(EntityUid uid, NukeopsRuleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        if (component.NukiePlanet != null)
            return true; // Map is already loaded.

        if (!component.SpawnOutpost)
            return true;

        var path = component.NukieOutpostMap;
        var shuttlePath = component.NukieShuttleMap;

        var mapId = _mapManager.CreateMap();
        var options = new MapLoadOptions
        {
            LoadMap = true,
        };

        if (!_map.TryLoad(mapId, path.ToString(), out var outpostGrids, options) || outpostGrids.Count == 0)
        {
            Logger.ErrorS("nukies", $"Error loading map {path} for nukies!");
            return false;
        }

        // Assume the first grid is the outpost grid.
        component.NukieOutpost = outpostGrids[0];

        // Listen I just don't want it to overlap.
        if (!_map.TryLoad(mapId, shuttlePath.ToString(), out var grids, new MapLoadOptions {Offset = Vector2.One*1000f}) || !grids.Any())
        {
            Logger.ErrorS("nukies", $"Error loading grid {shuttlePath} for nukies!");
            return false;
        }

        var shuttleId = grids.First();

        // Naughty, someone saved the shuttle as a map.
        if (Deleted(shuttleId))
        {
            Logger.ErrorS("nukeops", $"Tried to load nukeops shuttle as a map, aborting.");
            _mapManager.DeleteMap(mapId);
            return false;
        }

        if (TryComp<ShuttleComponent>(shuttleId, out var shuttle))
        {
            _shuttle.TryFTLDock(shuttleId, shuttle, component.NukieOutpost.Value);
        }

        component.NukiePlanet = mapId;
        component.NukieShuttle = shuttleId;
        return true;
    }

    private (string Name, string Role, string Gear) GetOperativeSpawnDetails(int spawnNumber, NukeopsRuleComponent component )
    {
        string name;
        string role;
        string gear;

        // Spawn the Commander then Agent first.
        switch (spawnNumber)
        {
            case 0:
                name = Loc.GetString("nukeops-role-commander") + " " + _random.PickAndTake(component.OperativeNames[component.EliteNames]);
                role = component.CommanderRolePrototype;
                gear = component.CommanderStartGearPrototype;
                break;
            case 1:
                name = Loc.GetString("nukeops-role-agent") + " " + _random.PickAndTake(component.OperativeNames[component.NormalNames]);
                role = component.MedicRoleProto;
                gear = component.MedicStartGearPrototype;
                break;
            default:
                name = Loc.GetString("nukeops-role-operator") + " " + _random.PickAndTake(component.OperativeNames[component.NormalNames]);
                role = component.OperativeRoleProto;
                gear = component.OperativeStartGearPrototype;
                break;
        }

        return (name, role, gear);
    }

    /// <summary>
    ///     Adds missing nuke operative components, equips starting gear and renames the entity.
    /// </summary>
    private void SetupOperativeEntity(EntityUid mob, string name, string gear, HumanoidCharacterProfile? profile, NukeopsRuleComponent component)
    {
        MetaData(mob).EntityName = name;
        EnsureComp<NukeOperativeComponent>(mob);

        if (profile != null)
        {
            _humanoidSystem.LoadProfile(mob, profile);
        }

        if (component.StartingGearPrototypes.TryGetValue(gear, out var gearPrototype))
            _stationSpawningSystem.EquipStartingGear(mob, gearPrototype, profile);

        _faction.RemoveFaction(mob, "NanoTrasen", false);
        _faction.AddFaction(mob, "Syndicate");
    }

    private void SpawnOperatives(int spawnCount, List<IPlayerSession> sessions, bool addSpawnPoints, NukeopsRuleComponent component)
    {
        if (component.NukieOutpost == null)
            return;

        var outpostUid = component.NukieOutpost.Value;
        var spawns = new List<EntityCoordinates>();

        // Forgive me for hardcoding prototypes
        foreach (var (_, meta, xform) in EntityManager.EntityQuery<SpawnPointComponent, MetaDataComponent, TransformComponent>(true))
        {
            if (meta.EntityPrototype?.ID != component.SpawnPointPrototype)
                continue;

            if (xform.ParentUid != component.NukieOutpost)
                continue;

            spawns.Add(xform.Coordinates);
            break;
        }

        if (spawns.Count == 0)
        {
            spawns.Add(EntityManager.GetComponent<TransformComponent>(outpostUid).Coordinates);
            Logger.WarningS("nukies", $"Fell back to default spawn for nukies!");
        }

        // TODO: This should spawn the nukies in regardless and transfer if possible; rest should go to shot roles.
        for(var i = 0; i < spawnCount; i++)
        {
            var spawnDetails = GetOperativeSpawnDetails(i, component);
            var nukeOpsAntag = _prototypeManager.Index<AntagPrototype>(spawnDetails.Role);

            if (sessions.TryGetValue(i, out var session))
            {
                var profile = _prefs.GetPreferences(session.UserId).SelectedCharacter as HumanoidCharacterProfile;
                if (!_prototypeManager.TryIndex(profile?.Species ?? SharedHumanoidAppearanceSystem.DefaultSpecies, out SpeciesPrototype? species))
                {
                    species = _prototypeManager.Index<SpeciesPrototype>(SharedHumanoidAppearanceSystem.DefaultSpecies);
                }

                var mob = EntityManager.SpawnEntity(species.Prototype, _random.Pick(spawns));
                SetupOperativeEntity(mob, spawnDetails.Name, spawnDetails.Gear, profile, component);
                var newMind = _mindSystem.CreateMind(session.UserId, spawnDetails.Name);
                _mindSystem.SetUserId(newMind, session.UserId);
                _mindSystem.AddRole(newMind, new NukeopsRole(newMind, nukeOpsAntag));

                _mindSystem.TransferTo(newMind, mob);
            }
            else if (addSpawnPoints)
            {
                var spawnPoint = EntityManager.SpawnEntity(component.GhostSpawnPointProto, _random.Pick(spawns));
                var ghostRole = EnsureComp<GhostRoleComponent>(spawnPoint);
                EnsureComp<GhostRoleMobSpawnerComponent>(spawnPoint);
                ghostRole.RoleName = Loc.GetString(nukeOpsAntag.Name);
                ghostRole.RoleDescription = Loc.GetString(nukeOpsAntag.Objective);

                var nukeOpSpawner = EnsureComp<NukeOperativeSpawnerComponent>(spawnPoint);
                nukeOpSpawner.OperativeName = spawnDetails.Name;
                nukeOpSpawner.OperativeRolePrototype = spawnDetails.Role;
                nukeOpSpawner.OperativeStartingGear = spawnDetails.Gear;
            }
        }
    }

    private void SpawnOperativesForGhostRoles(EntityUid uid, NukeopsRuleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!SpawnMap(uid, component))
        {
            Logger.InfoS("nukies", "Failed to load map for nukeops");
            return;
        }
        // Basically copied verbatim from traitor code
        var playersPerOperative = component.PlayersPerOperative;
        var maxOperatives = component.MaxOperatives;

        var playerPool = _playerSystem.ServerSessions.ToList();
        var numNukies = MathHelper.Clamp(playerPool.Count / playersPerOperative, 1, maxOperatives);

        var operatives = new List<IPlayerSession>();
        SpawnOperatives(numNukies, operatives, true, component);
    }

    //For admins forcing someone to nukeOps.
    public void MakeLoneNukie(Mind.Mind mind)
    {
        if (!mind.OwnedEntity.HasValue)
            return;

        //ok hardcoded value bad but so is everything else here
        _mindSystem.AddRole(mind, new NukeopsRole(mind, _prototypeManager.Index<AntagPrototype>("Nukeops")));
        SetOutfitCommand.SetOutfit(mind.OwnedEntity.Value, "SyndicateOperativeGearFull", EntityManager);
    }

    private void OnStartAttempt(RoundStartAttemptEvent ev)
    {
        var query = EntityQueryEnumerator<NukeopsRuleComponent, GameRuleComponent>();
        while (query.MoveNext(out var uid, out var nukeops, out var gameRule))
        {
            if (!GameTicker.IsGameRuleAdded(uid, gameRule))
                continue;

            var minPlayers = nukeops.MinPlayers;
            if (!ev.Forced && ev.Players.Length < minPlayers)
            {
                _chatManager.SendAdminAnnouncement(Loc.GetString("nukeops-not-enough-ready-players",
                    ("readyPlayersCount", ev.Players.Length), ("minimumPlayers", minPlayers)));
                ev.Cancel();
                continue;
            }

            if (ev.Players.Length != 0)
                continue;

            _chatManager.DispatchServerAnnouncement(Loc.GetString("nukeops-no-one-ready"));
            ev.Cancel();
        }
    }

    protected override void Started(EntityUid uid, NukeopsRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        // TODO: Loot table or something
        foreach (var proto in new[]
                 {
                     component.CommanderStartGearPrototype,
                     component.MedicStartGearPrototype,
                     component.OperativeStartGearPrototype
                 })
        {
            component.StartingGearPrototypes.Add(proto, _prototypeManager.Index<StartingGearPrototype>(proto));
        }

        foreach (var proto in new[] { component.EliteNames, component.NormalNames })
        {
            component.OperativeNames.Add(proto, new List<string>(_prototypeManager.Index<DatasetPrototype>(proto).Values));
        }

        // Add pre-existing nuke operatives to the credit list.
        var query = EntityQuery<NukeOperativeComponent, MindContainerComponent, MetaDataComponent>(true);
        foreach (var (_, mindComp, metaData) in query)
        {
            if (!mindComp.HasMind || !_mindSystem.TryGetSession(mindComp.Mind, out var session))
                continue;
            component.OperativePlayers.Add(metaData.EntityName, session);
        }

        if (GameTicker.RunLevel == GameRunLevel.InRound)
            SpawnOperativesForGhostRoles(uid, component);
    }

}
