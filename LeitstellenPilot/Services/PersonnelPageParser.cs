using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HtmlAgilityPack;
using LeitstellenPilot.Models;
using Newtonsoft.Json;

namespace LeitstellenPilot.Services;

public sealed record PersonnelPage(IReadOnlyList<StationPerson> People, IReadOnlyDictionary<long, string> Actions, long VehicleId, long BuildingId,
    string Token = "");
public sealed record PersonnelEditForm(string Path, string Token, int CurrentMaximum, int[] AllowedMaximums);
public static class PersonnelPageParser
{
    private static string Attr(HtmlNode node, string name) => HtmlEntity.DeEntitize(node.GetAttributeValue(name, "")) ?? "";
    private static bool Class(HtmlNode node, string name) => Attr(node, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(name);
    private static HtmlNode[] Nodes(HtmlNode root, string xpath) => root.SelectNodes(xpath)?.ToArray() ?? [];
    private static long Id(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) &&
        long.TryParse(value[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0;
    private static bool Path(string value, Uri origin, string expected) => Uri.TryCreate(origin, value, out var uri) &&
        uri.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == expected && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public static PersonnelPage Read(string html, long vehicleId, long buildingId, Uri origin)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        var root = page.DocumentNode;
        var back = root.SelectSingleNode("//a[@id='back_to_vehicle']");
        if (back == null || !Path(Attr(back, "href"), origin, $"/vehicles/{vehicleId}") ||
            !Nodes(root, "//ol[contains(@class,'breadcrumb')]//a").Any(a => Path(Attr(a, "href"), origin, $"/buildings/{buildingId}")))
            throw new FormatException("Personalzuweisung gehört nicht zur erwarteten Wache / zum Fahrzeug.");
        var table = root.SelectSingleNode("//table[@id='personal_table']") ?? throw new FormatException("Personalliste nicht erkannt.");
        var people = new List<StationPerson>(); var actions = new Dictionary<long, string>();
        foreach (var row in Nodes(table, ".//tr[starts-with(@id,'personal_')]"))
        {
            var id = Id(Attr(row, "id"), "personal_");
            var cells = Nodes(row, "./td");
            if (id <= 0 || cells.Length != 4 || !row.Attributes.Contains("data-filterable-by")) throw new FormatException("Unvollständige Personaldaten.");
            string[] training;
            try { training = JsonConvert.DeserializeObject<string[]>(Attr(row, "data-filterable-by")) ?? throw new FormatException("Ausbildungsdaten fehlen."); }
            catch (JsonException ex) { throw new FormatException("Ausbildungsdaten nicht erkannt.", ex); }
            if (training.Any(string.IsNullOrWhiteSpace)) throw new FormatException("Ungültige Ausbildungsdaten.");
            var bindings = Nodes(cells[3], ".//a[@href]").Select(a => Id(Attr(a, "href"), "/vehicles/")).Where(n => n > 0).Distinct().ToArray();
            if (bindings.Length > 1) throw new FormatException("Mehrdeutige Fahrzeugbindung.");
            var button = Nodes(cells[3], ".//a[@personal_id]").SingleOrDefault();
            if (button != null)
            {
                if (Attr(button, "personal_id") != id.ToString(CultureInfo.InvariantCulture) ||
                    !Path(Attr(button, "href"), origin, $"/vehicles/{vehicleId}/zuweisungDo/{id}") ||
                    Class(button, "btn-assigned") != (bindings.SingleOrDefault() == vehicleId))
                    throw new FormatException("Personalaktion passt nicht zur Fahrzeugbindung.");
                actions.Add(id, $"/vehicles/{vehicleId}/zuweisungDo/{id}");
            }
            var available = Nodes(cells[2], ".//span").Any(n => Class(n, "label-success"));
            if (available && button == null) throw new FormatException("Personalaktion fehlt für verfügbares Personal.");
            people.Add(new(id, training.Distinct(StringComparer.Ordinal).Order().ToArray(), available, bindings.Length == 0 ? null : bindings[0]));
        }
        if (people.Select(p => p.Id).Distinct().Count() != people.Count) throw new FormatException("Doppelte Personen in der Wache.");
        var csrf = Nodes(root, "//head/meta[@name='csrf-token']");
        if (csrf.Length > 1) throw new FormatException("Mehrdeutiger Sicherheitstoken für die Personalzuweisung.");
        return new(people, actions, vehicleId, buildingId, csrf.Length == 1 ? Attr(csrf[0], "content") : "");
    }

    public static PersonnelEditForm ReadEdit(string html, long vehicleId, Uri origin)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        var form = page.DocumentNode.SelectSingleNode($"//form[@id='edit_vehicle_{vehicleId}']");
        if (form == null || !Path(Attr(form, "action"), origin, $"/vehicles/{vehicleId}") ||
            !Attr(form, "method").Equals("post", StringComparison.OrdinalIgnoreCase) ||
            form.SelectSingleNode(".//input[@name='_method' and @value='patch']") == null)
            throw new FormatException("Fahrzeugformular nicht erkannt.");
        var token = form.SelectSingleNode(".//input[@name='authenticity_token']");
        var select = form.SelectSingleNode(".//select[@name='vehicle[personal_max]' and not(@disabled)]");
        if (token == null || Attr(token, "value").Length == 0 || select == null) throw new FormatException("Besatzungsformular ist unvollständig.");
        var options = Nodes(select, "./option[not(@disabled)]");
        var values = options.Select(o => int.TryParse(Attr(o, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).ToArray();
        var selected = options.Where(o => o.Attributes.Contains("selected")).ToArray();
        if (values.Length == 0 || values.Any(n => n < 1 || n > 100) || selected.Length > 1) throw new FormatException("Besatzungsgrenzen nicht erkannt.");
        var current = int.Parse(Attr(selected.SingleOrDefault() ?? options[0], "value"), CultureInfo.InvariantCulture);
        return new($"/vehicles/{vehicleId}", Attr(token, "value"), current, values);
    }

    public static IReadOnlyList<PersonnelEquipment> ReadAssignedEquipment(string html, long buildingId,
        IReadOnlyCollection<long> ownVehicleIds, Uri origin)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        if (page.DocumentNode.SelectSingleNode("//table[@id='vehicle_table']") == null)
            throw new FormatException("Gebäudeseite für die Beladungsprüfung nicht erkannt.");
        var equipment = new List<PersonnelEquipment>();
        var seen = new HashSet<long>();
        foreach (var form in Nodes(page.DocumentNode, "//form[contains(@action,'equipment_assign_to')]"))
        {
            var action = Attr(form, "action");
            if (!Uri.TryCreate(origin, action, out var uri)) throw new FormatException("Beladungsformular nicht erkannt.");
            var equipmentId = Id(uri.AbsolutePath, $"/equipment_assign_to/{buildingId}/");
            if (equipmentId == 0 || !Path(action, origin, $"/equipment_assign_to/{buildingId}/{equipmentId}") || !seen.Add(equipmentId))
                throw new FormatException("Beladungsformular gehört nicht eindeutig zur Wache.");
            var select = form.SelectSingleNode(".//select[@name='assigned_vehicle_id']") ?? throw new FormatException("Beladungszuweisung nicht erkannt.");
            var options = Nodes(select, "./option"); var selected = options.Where(o => o.Attributes.Contains("selected")).ToArray();
            if (selected.Length > 1 || options.Length == 0) throw new FormatException("Beladungszuweisung ist mehrdeutig.");
            var value = Attr(selected.SingleOrDefault() ?? options[0], "value");
            if (value is "" or "0") continue;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !ownVehicleIds.Contains(id))
                throw new FormatException("Beladung gehört nicht zum bestätigten Fahrzeugbestand der Wache.");
            var caption = form.Ancestors("tr").FirstOrDefault()?.SelectSingleNode("./td[1]") ?? throw new FormatException("Bezeichnung der Beladung fehlt.");
            var name = (HtmlEntity.DeEntitize(string.Concat(caption.ChildNodes.TakeWhile(n => n.Name != "br").Select(n => n.InnerText))) ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new FormatException("Bezeichnung der Beladung fehlt.");
            equipment.Add(new(equipmentId, id, name));
        }
        return equipment.OrderBy(e => e.Id).ToArray();
    }
}
