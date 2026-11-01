using System;
using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed class VehiclePlanner
{
    private readonly IReadOnlyDictionary<string, JObject> _metadata;
    private readonly IReadOnlyDictionary<string, string> _labels;
    private readonly DispatchRuleCatalog _rules;
    private readonly int _maxApproachMinutes;
    public VehiclePlanner(IReadOnlyDictionary<string, JObject> metadata, IReadOnlyDictionary<string, string>? labels = null, DispatchRuleCatalog? rules = null, int maxApproachMinutes = 0)
    {
        _metadata = metadata;
        _labels = labels ?? new Dictionary<string, string>();
        _rules = rules ?? DispatchRuleCatalog.Create(DispatchRuleCatalog.BundledDefinition(), metadata);
        _maxApproachMinutes = ApproachPolicy.NormalizeMinutes(maxApproachMinutes);
    }

    public DispatchPlan Build(Mission mission, EinsatzInfo? specification, MissionDetails details,
        IReadOnlyCollection<Vehicle> inventory, ISet<long>? reserved = null, IReadOnlyDictionary<long, int>? stationCrew = null)
    {
        var plan = new DispatchPlan { MissionId = mission.Id };
        bool IsPoliceReplacementDemand(string key) => key == "police_cars" || details.HasMissingRequirements &&
            specification?.Requirements.GetValueOrDefault("police_cars") is { Type: JTokenType.Integer } patrol && patrol.Value<int>() > 0 &&
            key is "oneof_police_patrol_or_motorcycle" or "oneof_police_patrol_or_civil_patrol" or "oneof_police_patrol_or_civil_patrol_or_motorcycle";
        bool AllowsReplacementType(string key, int type) => key == "police_cars" || _rules.Types[key].Contains(type);
        bool MatchesDemand(string key, Vehicle vehicle) => IsPoliceReplacementDemand(key)
            ? vehicle.TypeId is 95 or 98 ? AllowsReplacementType(key, vehicle.TypeId) && PoliceReplacementLimit(specification, vehicle.TypeId) > 0
                : _rules.Types["police_cars"].Contains(vehicle.TypeId)
            : Matches(key, vehicle, specification);
        var remainingCrew = stationCrew?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<long, int>();
        var allocatedCrew = new Dictionary<long, int>();
        int PlannedCrew(Vehicle vehicle) => allocatedCrew.TryGetValue(vehicle.Id, out var allocated) ? allocated :
            remainingCrew.TryGetValue(vehicle.BuildingId, out var available) && vehicle.Personnel == 0
                ? Math.Min(available, Math.Max(0, vehicle.MaximumPersonnel ?? (_metadata.GetValueOrDefault(vehicle.TypeId.ToString())?.Value<int?>("maxPersonnel") ?? 1))) :
            Math.Min(vehicle.Personnel, Math.Max(0, vehicle.MaximumPersonnel ?? vehicle.Personnel));
        bool CanStaff(Vehicle vehicle) => !remainingCrew.ContainsKey(vehicle.BuildingId) ||
            PlannedCrew(vehicle) >= (_metadata.GetValueOrDefault(vehicle.TypeId.ToString())?.Value<int?>("minPersonnel") ?? 1);
        void AllocateCrew(Vehicle vehicle)
        {
            if (vehicle.Personnel > 0 || allocatedCrew.ContainsKey(vehicle.Id) || !remainingCrew.ContainsKey(vehicle.BuildingId)) return;
            var count = PlannedCrew(vehicle); allocatedCrew[vehicle.Id] = count; remainingCrew[vehicle.BuildingId] -= count;
        }
        int VehicleCapacity(string key, Vehicle vehicle) => key.StartsWith("_personnel_") && vehicle.IsAvailable &&
            vehicle.Personnel == 0 && remainingCrew.ContainsKey(vehicle.BuildingId) && CrewMatches(key, vehicle, specification)
                ? PlannedCrew(vehicle) : Capacity(key, vehicle, details, specification);
        int TravelCost(Vehicle vehicle) => details.VehicleTravelSeconds.GetValueOrDefault(vehicle.Id, int.MaxValue);
        if (specification == null)
        {
            if (details.HasMissingRequirementsFromPage && details.MissingRequirements.Count > 0 && details.UnparsedRequirements.Count == 0 &&
                details.MissingRequirements.All(demand => !demand.Key.StartsWith("oneof_police_", StringComparison.Ordinal)))
                plan.Notes.Add("Katalogvariante unbekannt; ausschließlich die aktuelle eindeutige Nachforderung der Einsatzseite planen.");
            else plan.AddRecognitionProblem("Einsatztyp / Variante konnte nicht eindeutig zugeordnet werden.");
        }
        var demands = new Dictionary<string, int>();
        var missingOnly = details.HasMissingRequirements && details.MissingRequirements.Count > 0;
        if (missingOnly)
            foreach (var demand in details.MissingRequirements) demands[demand.Key] = demands.GetValueOrDefault(demand.Key) + demand.Count;
        else if (specification != null)
        {
            foreach (var pair in specification.Requirements)
            {
                if (pair.Value.Type == JTokenType.Integer)
                {
                    var onlyIfPresent = pair.Key switch
                    {
                        "k9" => "need_k9_only_if_guard_dogs_present",
                        "helicopter_bucket" => "need_helicopter_bucket_only_if_present",
                        _ => ""
                    };
                    if (onlyIfPresent.Length > 0 && specification.Additional.Value<bool?>(onlyIfPresent) == true)
                    {
                        plan.Notes.Add($"{Label(pair.Key)}: nur bei tatsächlicher Nachforderung einplanen (bedingt durch vorhandene Ausrüstung).");
                        continue;
                    }
                    if (specification.Chances.TryGetValue(pair.Key, out var chance) && chance < 100)
                    {
                        plan.Notes.Add($"{Label(pair.Key)}: mögliche Anforderung ({chance} %), erst bei tatsächlicher Nachforderung einplanen.");
                        continue;
                    }
                    var count = pair.Value.Value<int>();
                    if (count > 0) demands[pair.Key] = count;
                }
                else if (pair.Key == "personnel_educations" && pair.Value is JObject)
                    continue;
                else if (pair.Value.Type != JTokenType.Null)
                    plan.AddRecognitionProblem($"Anforderung {pair.Key} benötigt eine manuelle Prüfung.");
            }
            foreach (var (field, key) in new[] { ("average_min_fire_personnel", "_personnel_fire"),
                         ("average_min_police_personnel", "_personnel_police"), ("average_min_thw_personnel", "_personnel_thw"),
                         ("average_min_ambulance_personnel", "_personnel_ambulance") })
            {
                var count = specification.Additional.Value<int?>(field) ?? 0;
                if (count > 0) demands[key] = count;
            }
            var education = specification.Requirements.GetValueOrDefault("personnel_educations") as JObject ??
                specification.Additional["personnel_educations"] as JObject;
            var unknownEducation = new List<JProperty>();
            foreach (var course in education?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                if (TrainedPersonnelRequirements.TryCourse(course.Name, out var key, _rules) && course.Value.Type == JTokenType.Integer &&
                    int.TryParse(course.Value.ToString(), out var count) && count > 0)
                    demands[key] = count;
                else unknownEducation.Add(course);
            }
            if (unknownEducation.Count > 0)
                plan.AddRecognitionProblem("Besatzungsausbildung prüfen: " + string.Join(", ",
                    (specification.Additional["personnel_educations"] as JObject)?.Properties().Select(p => $"{p.Name}: {p.Value}") ??
                    unknownEducation.Select(p => $"{p.Name}: {p.Value}")) + ".");
            var patients = mission.patients_count;
            // Some rescue calls create their patient only after the initial crew arrives.
            // The catalog's guaranteed minimum can start those calls without inventing
            // requirements for unknown types or reusing it after treatment has begun.
            if (patients <= 0 && mission.vehicle_state == 0 && !details.HasPatients && !details.HasMissingRequirements &&
                specification.Requirements.Count == 0 && specification.Additional.Value<string>("filter_id") == "ambulance_station_missions" &&
                specification.Additional["possible_patient_min"] is { Type: JTokenType.Integer } minimum &&
                int.TryParse(minimum.ToString(), out var guaranteed) && guaranteed > 0)
            {
                patients = guaranteed;
                plan.Notes.Add($"Patienten noch nicht angezeigt: Mindestbedarf laut Rettungseinsatz-Katalog ({patients}) für die Erstalarmierung verwenden.");
            }
            if (patients > 0)
            {
                demands["ambulances"] = Math.Max(demands.GetValueOrDefault("ambulances"), patients);
                if (specification.Chances.GetValueOrDefault("nef") >= 100)
                    demands["nef"] = patients;
                else if (specification.Chances.GetValueOrDefault("nef") > 0)
                    plan.Notes.Add("Notarzt nur bei tatsächlicher Nachforderung zusätzlich einplanen.");
                plan.Notes.Add("Patiententransporte und Sprechwünsche werden im Spiel bearbeitet.");
            }
        }
        foreach (var demand in TrainedPersonnelRequirements.Demands(details, specification, _rules))
            demands[demand.Key] = Math.Max(demands.GetValueOrDefault(demand.Key), demand.Value);
        foreach (var text in details.UnparsedRequirements) plan.AddRecognitionProblem("Unbekannte Nachforderung: " + text);
        if (details.HasPrisonerTransportRequest) plan.Notes.Add("Gefangenentransport offen; wird getrennt von der Fahrzeugalarmierung bearbeitet.");
        if (details.HasMissingRequirements && details.MissingRequirements.Count == 0 && details.UnparsedRequirements.Count == 0)
            plan.AddRecognitionProblem("Die Nachforderung ist nicht lesbar. Bitte im Spiel prüfen.");
        foreach (var pair in demands) plan.Requirements.Add(new(pair.Key, pair.Value));
        if (demands.Count == 0) plan.AddRecognitionProblem("Keine verlässlich ermittelten Fahrzeuganforderungen.");
        var safePartialTopup = details.HasMissingRequirementsFromPage && (mission.vehicle_state > 0 || details.AssignedVehicleIds.Count > 0) && plan.Problems.Count == 0;

        var assigned = inventory.Where(v => missingOnly
                ? details.IsEnRouteTo(v,mission.Id)
                : v.TargetType == "mission" && v.TargetId == mission.Id || details.AssignedVehicleIds.Contains(v.Id))
            .Select(v => v.Id).ToHashSet();
        var restrictedSelection = specification?.Additional["vehicle_groups"] is JArray groups && groups.Count > 0;
        bool ServerOffers(Vehicle vehicle) => !restrictedSelection || details.VehicleOrder.ContainsKey(vehicle.Id);
        bool InRange(Vehicle vehicle) => ApproachPolicy.Allows(vehicle.Id, details, _maxApproachMinutes);
        if (restrictedSelection) plan.Notes.Add("Fahrzeugauswahl für diesen Einsatz eingeschränkt: nur vom Spiel angebotene freie Fahrzeuge verwenden.");
        var candidates = inventory.Where(v => assigned.Contains(v.Id) ||
            v.IsAvailable && InRange(v) && ServerOffers(v) && !details.DisabledVehicleIds.Contains(v.Id) && reserved?.Contains(v.Id) != true && HasPersonnel(v, details))
            .OrderBy(v => assigned.Contains(v.Id) ? 0 : 1).ThenBy(v => v.Order).ThenBy(v => v.Id).ToList();
        var counts = new Dictionary<string, int>();
        foreach (var pair in demands.Where(p => !IsCapacity(p.Key)))
        {
            if (!_rules.Types.ContainsKey(pair.Key))
            {
                safePartialTopup = false;
                plan.AddRecognitionProblem(pair.Key is "care_service" or "care_service_equipment" or "_care_staff" or "_catering_staff"
                    ? $"Betreuung/Verpflegung: {pair.Value} {Label(pair.Key)} noch nicht automatisch planbar. Bitte im Spiel disponieren."
                    : $"Nicht unterstützte Anforderung: {Label(pair.Key)} ({pair.Value}).");
                continue;
            }
            if (pair.Value > 500) { safePartialTopup = false; plan.AddRecognitionProblem("Ungewöhnlich hohe Fahrzeuganzahl: " + pair.Key); continue; }
            counts[pair.Key] = pair.Value;
        }
        // Requirements are capability counts, not mutually exclusive vehicle slots.
        // A patrol car fulfills both "patrol car" and "patrol car or motorcycle";
        // likewise a HLF can supply both fire-engine and rescue capability.
        var assignedCandidates = candidates.Where(vehicle => assigned.Contains(vehicle.Id)).ToArray();
        var outsideScope = inventory.Where(vehicle => !assigned.Contains(vehicle.Id) &&
            (vehicle.TargetType == "mission" && vehicle.TargetId == mission.Id || details.AssignedVehicleIds.Contains(vehicle.Id))).ToArray();
        var motorcycles = assignedCandidates.Count(vehicle => vehicle.TypeId == 95);
        var civilPatrols = assignedCandidates.Count(vehicle => vehicle.TypeId == 98);
        var outsideMotorcycles = outsideScope.Count(vehicle => vehicle.TypeId == 95);
        var outsideCivilPatrols = outsideScope.Count(vehicle => vehicle.TypeId == 98);
        var ordinaryPatrols = assignedCandidates.Count(vehicle => _rules.Types["police_cars"].Contains(vehicle.TypeId));
        int SpecificRequirement(int type)
        {
            var key = type == 95 ? "police_motorcycle" : "civil_patrolcar";
            if (!missingOnly) return counts.GetValueOrDefault(key);
            var declared = specification?.Requirements.GetValueOrDefault(key);
            var catalogCount = declared?.Type == JTokenType.Integer ? Math.Max(0, declared.Value<int>()) : 0;
            return Math.Max(catalogCount, counts.ContainsKey(key) ? counts[key] + (type == 95 ? outsideMotorcycles : outsideCivilPatrols) : 0);
        }
        var specificMotorcycles = SpecificRequirement(95); var specificCivilPatrols = SpecificRequirement(98);
        var motorcycleLimit = PoliceReplacementLimit(specification, 95); var civilPatrolLimit = PoliceReplacementLimit(specification, 98);
        int ReplacementCoverage(int type, int scopeCount)
        {
            var outside = type == 95 ? outsideMotorcycles : outsideCivilPatrols;
            var specific = type == 95 ? specificMotorcycles : specificCivilPatrols;
            var limit = type == 95 ? motorcycleLimit : civilPatrolLimit;
            return Math.Min(limit, Math.Max(0, outside + scopeCount - specific)) - Math.Min(limit, Math.Max(0, outside - specific));
        }
        int PoliceCoverage(string key) => ordinaryPatrols + (AllowsReplacementType(key, 95) ? ReplacementCoverage(95, motorcycles) : 0) +
            (AllowsReplacementType(key, 98) ? ReplacementCoverage(98, civilPatrols) : 0);
        var covered = counts.Keys.ToDictionary(key => key, key => IsPoliceReplacementDemand(key) ? PoliceCoverage(key) : assignedCandidates.Count(vehicle => MatchesDemand(key, vehicle)));
        bool AddsCoverage(string key, Vehicle vehicle) => MatchesDemand(key, vehicle) && (!IsPoliceReplacementDemand(key) || vehicle.TypeId switch
        {
            95 => ReplacementCoverage(95, motorcycles + 1) > ReplacementCoverage(95, motorcycles),
            98 => ReplacementCoverage(98, civilPatrols + 1) > ReplacementCoverage(98, civilPatrols),
            _ => true
        });
        var selected = new List<Vehicle>();
        var available = candidates.Where(v => !assigned.Contains(v.Id)).ToList();
        while (counts.Any(demand => covered[demand.Key] < demand.Value))
        {
            var vehicle = available.Where(CanStaff).OrderByDescending(v => counts.Count(demand =>
                    covered[demand.Key] < demand.Value && AddsCoverage(demand.Key, v)))
                .ThenBy(TravelCost).ThenBy(v => v.Order).ThenBy(v => v.Id).FirstOrDefault();
            if (vehicle == null || !counts.Any(demand => covered[demand.Key] < demand.Value && AddsCoverage(demand.Key, vehicle))) break;
            selected.Add(vehicle);
            AllocateCrew(vehicle);
            available.Remove(vehicle);
            if (vehicle.TypeId == 95) motorcycles++;
            if (vehicle.TypeId == 98) civilPatrols++;
            if (_rules.Types["police_cars"].Contains(vehicle.TypeId)) ordinaryPatrols++;
            foreach (var key in counts.Keys.Where(key => MatchesDemand(key, vehicle)))
                covered[key] = IsPoliceReplacementDemand(key) ? PoliceCoverage(key) : covered[key] + 1;
        }
        foreach (var demand in counts)
        {
            var fulfilled = Math.Min(covered[demand.Key], demand.Value);
            plan.Notes.Add($"{Label(demand.Key)}: {fulfilled}/{demand.Value}");
            if (fulfilled < demand.Value)
            {
                plan.Shortfalls.Add(new(demand.Key, Label(demand.Key), demand.Value - fulfilled,
                    _rules.Types[demand.Key].Concat(inventory.Where(v => MatchesDemand(demand.Key, v)).Select(v => v.TypeId)).Distinct().ToArray()));
                plan.Problems.Add($"{demand.Value - fulfilled} × {Label(demand.Key)} fehlen.");
                foreach (var vehicle in inventory.Where(vehicle => MatchesDemand(demand.Key, vehicle) && !assigned.Contains(vehicle.Id)).Take(10))
                {
                    var reason = vehicle.QueuedMissionId.GetValueOrDefault() > 0 ? "bereits für einen Folgeeinsatz gebunden" :
                        !vehicle.IsAvailable ? $"nicht frei (FMS {vehicle.State})" :
                        !ServerOffers(vehicle) ? "für diesen Einsatz vom Spiel nicht angeboten" :
                        details.DisabledVehicleIds.Contains(vehicle.Id) ? "Auswahl vom Spiel gesperrt" :
                        reserved?.Contains(vehicle.Id) == true ? "vorübergehend reserviert" :
                        !InRange(vehicle) ? ApproachPolicy.Exclusion(vehicle.Id, details, _maxApproachMinutes) :
                        !HasPersonnel(vehicle, details) ? "Besatzung nicht bestätigt; keine ausreichende gebundene Besatzung und keine auswählbare Zeile auf der Einsatzseite" :
                        !CanStaff(vehicle) ? "Personal der Wache reicht für diese Einsatzgruppe nicht" : null;
                    if (reason != null) plan.Notes.Add($"{Label(demand.Key)} · {vehicle.Name}: {reason}.");
                }
            }
        }
        if (counts.ContainsKey("police_cars"))
            foreach (var (type, label) in new[] { (95, "Polizeimotorräder"), (98, "Zivilstreifenwagen") })
            {
                var limit = PoliceReplacementLimit(specification, type);
                if (limit > 0) plan.Notes.Add($"Erlaubter Ersatz für Funkstreifenwagen: {label}, " +
                    (limit == 500 ? "gemäß Einsatzfreigabe" : $"höchstens {limit}") + "; bereits zugeordnetes und separat gefordertes Gerät berücksichtigt.");
            }

        foreach (var pair in demands.Where(p => IsCapacity(p.Key)))
        {
            var contributing = candidates.Where(v => assigned.Contains(v.Id) || selected.Any(s => s.Id == v.Id)).ToList();
            var amount = contributing.Sum(v => VehicleCapacity(pair.Key, v));
            var trained=_rules.Trainings.TryGetValue(pair.Key,out var training);
            var unconfirmed=trained?assignedCandidates.Sum(vehicle=>details.UnconfirmedTrainedCrewByVehicleId.GetValueOrDefault(vehicle.Id)?.GetValueOrDefault(training!.Course)??0):0;
            // Resolve the training of an already travelling crew before sending
            // another vehicle solely for a possibly covered personnel shortage.
            while (amount + unconfirmed < pair.Value)
            {
                var remaining = pair.Value - amount - unconfirmed;
                var vehicle = candidates.Where(v => !contributing.Any(c => c.Id == v.Id) && selected.All(s => s.Id != v.Id) && CanStaff(v) && VehicleCapacity(pair.Key, v) > 0)
                    .OrderByDescending(v => VehicleCapacity(pair.Key, v) >= remaining)
                    .ThenByDescending(v => VehicleCapacity(pair.Key, v) >= remaining ? 0 : VehicleCapacity(pair.Key, v))
                    .ThenBy(TravelCost).ThenBy(v => v.Order).ThenBy(v => v.Id).FirstOrDefault();
                if (vehicle == null) break;
                selected.Add(vehicle);
                AllocateCrew(vehicle);
                amount += VehicleCapacity(pair.Key, vehicle);
            }
            var travelling=trained?contributing.Where(vehicle=>details.IsEnRouteTo(vehicle,mission.Id)).Sum(vehicle=>VehicleCapacity(pair.Key,vehicle)):0;
            var newlySelected=trained?selected.DistinctBy(vehicle=>vehicle.Id).Sum(vehicle=>VehicleCapacity(pair.Key,vehicle)):0;
            var status=new List<string>();
            if (travelling>0) status.Add($"{travelling} auf Anfahrt");
            if (newlySelected>0) status.Add($"{newlySelected} ausgewählt");
            if (unconfirmed>0 && amount<pair.Value) status.Add($"bis zu {unconfirmed} weitere auf Anfahrt, Ausbildung noch nicht bestätigt");
            plan.Notes.Add($"{Label(pair.Key)}: {amount}/{pair.Value}"+(status.Count>0?" · "+string.Join(" · ",status):""));
            if (pair.Key.StartsWith("_personnel_") && selected.Any(v => CrewMatches(pair.Key, v, specification) &&
                !details.CrewByVehicleId.ContainsKey(v.Id) && v.Personnel == 0))
                plan.Notes.Add("Automatische Besatzung anhand der Fahrzeugkapazität geplant; tatsächliches Personal wird beim nächsten Status geprüft.");
            if (amount < pair.Value)
            {
                var missing=Math.Max(0,pair.Value-amount-unconfirmed);
                if (missing > 0 && (trained || pair.Key.StartsWith("_personnel_")))
                    plan.Shortfalls.Add(new(pair.Key, Label(pair.Key), missing,
                        trained ? training!.VehicleTypes : [], Personnel: true));
                if (missing>0) plan.Problems.Add($"{missing} {Label(pair.Key)} fehlen.");
                if (unconfirmed>0) plan.Problems.Add($"{Label(pair.Key)}: Ausbildung der anfahrenden Besatzung noch nicht bestätigt; vor weiterer Nachalarmierung prüfen.");
            }
        }

        var usedTowingVehicles = new HashSet<long>();
        foreach (var vehicle in selected.ToList())
        {
            var towingTypes = TowingTypes(vehicle);
            if (towingTypes.Length == 0) continue;
            var towing = inventory.Where(v => towingTypes.Contains(v.TypeId) && !usedTowingVehicles.Contains(v.Id) &&
                (v.IsAvailable && InRange(v) && ServerOffers(v) || assigned.Contains(v.Id)) && HasPersonnel(v, details) && (assigned.Contains(v.Id) || CanStaff(v)) &&
                !details.DisabledVehicleIds.Contains(v.Id) && reserved?.Contains(v.Id) != true &&
                (vehicle.TowingVehicleId.HasValue ? v.Id == vehicle.TowingVehicleId : v.BuildingId == vehicle.BuildingId))
                .OrderBy(v => selected.Any(s => s.Id == v.Id) ? 0 : 1).ThenBy(v => v.Order).FirstOrDefault();
            if (towing == null) { safePartialTopup = false; plan.Problems.Add("Kein geeignetes Zugfahrzeug für " + vehicle.Name); }
            else
            {
                usedTowingVehicles.Add(towing.Id);
                if (!assigned.Contains(towing.Id) && selected.All(v => v.Id != towing.Id)) { selected.Add(towing); AllocateCrew(towing); }
            }
        }
        plan.SelectedVehicles.AddRange(selected.DistinctBy(v => v.Id));
        foreach (var pair in allocatedCrew) plan.AutomaticCrewAllocations[pair.Key] = pair.Value;
        if (!plan.IsComplete && remainingCrew.Count > 0 && inventory.Any(vehicle => vehicle.IsAvailable && !assigned.Contains(vehicle.Id) &&
            InRange(vehicle) && ServerOffers(vehicle) && !details.DisabledVehicleIds.Contains(vehicle.Id) && reserved?.Contains(vehicle.Id) != true && !CanStaff(vehicle) &&
            demands.Keys.Any(key => _rules.Types.ContainsKey(key) && MatchesDemand(key, vehicle) || key.StartsWith("_personnel_") && CrewMatches(key, vehicle, specification))))
        {
            plan.WaitingForPersonnel = "Wartet auf Personal: Für die Einsatzgruppe ist auf der Wache noch nicht genug Personal frei. Rückkehr und freien Personalbestand prüfen.";
            plan.Notes.Add(plan.WaitingForPersonnel);
        }
        if (allocatedCrew.Count > 0) plan.Notes.Add("Personal der Wache für die Einsatzgruppe eingeplant. Besatzungen im Einsatz oder auf Rückfahrt sind noch nicht wieder frei.");
        plan.ApproachLimited = !plan.IsComplete && _maxApproachMinutes > 0 && inventory.Any(vehicle => vehicle.IsAvailable && !assigned.Contains(vehicle.Id) && !InRange(vehicle) &&
            (demands.Keys.Any(key => _rules.Types.ContainsKey(key) && MatchesDemand(key, vehicle) || IsCapacity(key) && VehicleCapacity(key, vehicle) > 0) ||
             selected.Any(trailer => TowingTypes(trailer).Contains(vehicle.TypeId))));
        if (plan.ApproachLimited)
            plan.Notes.Add($"Maximale Anfahrt: {_maxApproachMinutes} Min. Längere oder noch unbekannte Anfahrten werden zurückgestellt; bereits zugeordnete Fahrzeuge bleiben berücksichtigt.");
        if (plan.SelectedVehicles.Count > 0)
            plan.Notes.Add("Anfahrt: " + string.Join(", ", plan.SelectedVehicles.Select(vehicle => vehicle.Name + ": " +
                (details.VehicleTravelSeconds.TryGetValue(vehicle.Id, out var seconds) ? seconds == 0 ? "sofort" : $"{seconds / 60}:{seconds % 60:00} Min." : "Fahrzeit unbekannt · Spielreihenfolge"))));
        if (missingOnly && assigned.Count > 0)
            plan.Notes.Add("Bereits anfahrende Fahrzeuge und deren Besatzung/Wasser sind in der Nachforderung eingeplant.");
        plan.CanSendPartialTopup = safePartialTopup && !plan.IsComplete && plan.SelectedVehicles.Count > 0;
        if (plan.CanSendPartialTopup) plan.Notes.Add("Verfügbare passende Kräfte zum aktiven Einsatz nachalarmieren; verbleibender Bedarf bleibt offen.");
        return plan;
    }

    private string Label(string key) => _rules.Label(key, _labels);
    public static int PoliceReplacementLimit(EinsatzInfo? specification, int type)
    {
        var field = type switch { 95 => "max_police_motorcycle_replacing_police_cars", 98 => "max_civil_patrol_replacing_police_cars", _ => "" };
        if (field.Length == 0 || specification == null) return 0;
        var value = specification.Additional[field];
        if (value != null && value.Type != JTokenType.Null)
            return value.Type == JTokenType.Integer && int.TryParse(value.ToString(), out var limit) ? Math.Clamp(limit, 0, 500) : 0;
        return type == 95 && specification.Additional["allow_police_motorcycle_instead_of_fustw"]?.Type == JTokenType.Boolean &&
            specification.Additional.Value<bool>("allow_police_motorcycle_instead_of_fustw") ? 500 : 0;
    }
    private bool HasPersonnel(Vehicle vehicle, MissionDetails details) =>
        // assigned_personnel_count counts bound staff, not automatic staffing from the station.
        _metadata.TryGetValue(vehicle.TypeId.ToString(), out var data) &&
        (details.VehicleOrder.ContainsKey(vehicle.Id) || vehicle.Personnel >= Math.Max(data.Value<int?>("minPersonnel") ?? 1,
            _rules.CrewOverrides[vehicle.TypeId.ToString()]?.Value<int?>("minimum") ?? 0));
    private bool Matches(string key, Vehicle vehicle, EinsatzInfo? specification = null) =>
        _rules.Types[key].Contains(vehicle.TypeId) || key == "ambulances" &&
        (vehicle.TypeId == 38 && specification?.Additional.Value<bool?>("allow_ktw_instead_of_rtw") == true ||
         vehicle.TypeId == 58 && specification?.Additional.Value<bool?>("allow_ktw_b_instead_of_rtw") == true) ||
        key == "firetrucks" && vehicle.TypeId == 75 && specification?.Additional.Value<bool?>("allow_arff_instead_of_lf") == true;
    private bool IsCapacity(string key) => key is "water_needed" or "foam_needed" or "min_pump_speed" or
        "water_damage_pump" || key.StartsWith("_personnel_") || _rules.Trainings.ContainsKey(key);
    private bool CrewMatches(string key, Vehicle vehicle, EinsatzInfo? specification)
    {
        if (Matches(key, vehicle)) return true;
        if (!_metadata.TryGetValue(vehicle.TypeId.ToString(), out var data)) return false;
        var buildings = data["possibleBuildings"]?.ToObject<int[]>() ?? [];
        // Being allowed at a fire station does not turn a RTW/NEF crew into
        // firefighters. Only unambiguously fire-service types extend this group.
        var service = buildings.Length == 0 ? "" :
            buildings.All(building => building is 0 or 18) ? "fire" :
            buildings.All(building => building is 6 or 11 or 13 or 17 or 19 or 22 or 24 or 29) ? "police" :
            buildings.All(building => building == 9) ? "thw" : "";
        var requested = key.Replace("_personnel_", "");
        if (service == requested) return true;
        return service.Length > 0 && specification?.Additional["personnel_inclusions"] is JObject inclusions &&
            inclusions[service] is JArray targets && targets.Values<string>().Contains(requested);
    }
    private int Capacity(string key, Vehicle vehicle, MissionDetails details, EinsatzInfo? specification)
    {
        if (_rules.Trainings.ContainsKey(key)) return TrainedPersonnelRequirements.ConfirmedCrew(key, vehicle, details, _rules);
        if (key.StartsWith("_personnel_"))
        {
            if (!CrewMatches(key, vehicle, specification)) return 0;
            if (details.CrewByVehicleId.TryGetValue(vehicle.Id, out var actual)) return actual;
            if (vehicle.Personnel > 0) return vehicle.MaximumPersonnel.HasValue
                ? Math.Min(vehicle.Personnel, Math.Max(0, vehicle.MaximumPersonnel.Value)) : vehicle.Personnel;
            // Bound staff being zero does not mean an automatically staffed vehicle is empty.
            if (!details.VehicleOrder.ContainsKey(vehicle.Id) || details.DisabledVehicleIds.Contains(vehicle.Id)) return 0;
            return _metadata.TryGetValue(vehicle.TypeId.ToString(), out var type)
                ? Math.Max(0, vehicle.MaximumPersonnel ?? type.Value<int?>("maxPersonnel") ?? 0) : 0;
        }
        if (!_metadata.TryGetValue(vehicle.TypeId.ToString(), out var data)) return 0;
        return key switch
        {
            "water_needed" => data.Value<int?>("waterTank") ?? 0,
            "foam_needed" => data.Value<int?>("foamTank") ?? 0,
            "min_pump_speed" => data.Value<int?>("pumpCapacity") ?? 0,
            "water_damage_pump" => (data.Value<string>("pumpType") == "fire" || vehicle.TypeId is 101 or 102) && (data.Value<int?>("pumpCapacity") ?? 0) > 0 ? 1 : 0,
            _ => 0
        };
    }
    private int[] TowingTypes(Vehicle vehicle)
    {
        if (!_metadata.TryGetValue(vehicle.TypeId.ToString(), out var data)) return [];
        if ((data.Value<string>("caption") ?? "").StartsWith("AB-")) return [46];
        return data.Value<bool?>("isTrailer") == true ? data["tractiveVehicles"]?.ToObject<int[]>() ?? [] : [];
    }
}
