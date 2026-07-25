using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SpaceCraft;

namespace ATLAS
{
    /// <summary>
    /// Part B — the content surface. Snapshots the GroupsHandler roster and the GroupData fields
    /// that, when they change, break something specific: a recategorised construction menu, a
    /// removed ingredient, a null craftableInList. Structural updates (Part A) and content updates
    /// break mods differently, and one scan cannot see both.
    ///
    /// Runtime read only: it reads the live group list and writes nothing to game or save state.
    /// </summary>
    internal static class ContentDrift
    {
        internal sealed class CaptureResult
        {
            public bool Available;
            public readonly List<ContentGroupRow> Groups = new List<ContentGroupRow>();
            public readonly List<DriftFinding> NullFindings = new List<DriftFinding>();
        }

        // Field keys. Kept as constants so the writer and the diff never disagree on a name.
        private const string FType = "type";
        private const string FUnlockWU = "unlockingWorldUnit";
        private const string FUnlockVal = "unlockingValue";
        private const string FTradeCat = "tradeCategory";
        private const string FTradeVal = "tradeValue";
        private const string FInvSize = "inventorySize";
        private const string FRecipe = "recipe";
        private const string FAssoc = "associatedGameObject";
        private const string FGroupCat = "groupCategory";
        private const string FRotFixed = "rotationFixed";
        private const string FNextTier = "nextTierGroup";
        private const string FItemCat = "itemCategory";
        private const string FItemSub = "itemSubCategory";
        private const string FEquip = "equipableType";
        private const string FUsable = "usableType";
        private const string FCraftable = "craftableInList";   // literal "null" when null (a standing landmine)

        // ── capture ────────────────────────────────────────────────────────────────────

        public static CaptureResult Capture()
        {
            var result = new CaptureResult();

            List<Group> groups;
            try { groups = GroupsHandler.GetAllGroups(); }
            catch { result.Available = false; return result; }

            if (groups == null) { result.Available = false; return result; }
            result.Available = true;

            foreach (var group in groups)
            {
                if (group == null) continue;

                GroupData data;
                string id;
                try { id = group.GetId(); data = group.GetGroupData(); }
                catch { continue; }
                if (data == null || string.IsNullOrEmpty(id)) continue;

                var row = new ContentGroupRow { Id = id };
                var f = row.Fields;

                var isItem = data is GroupDataItem;
                var isCons = data is GroupDataConstructible;
                row.ConcreteType = isCons ? "Constructible" : isItem ? "Item" : "Group";
                f[FType] = row.ConcreteType;

                f[FUnlockWU] = EnumName(() => data.unlockingWorldUnit);
                f[FUnlockVal] = data.unlockingValue.ToString(CultureInfo.InvariantCulture);
                f[FTradeCat] = EnumName(() => data.tradeCategory);
                f[FTradeVal] = data.tradeValue.ToString(CultureInfo.InvariantCulture);
                f[FInvSize] = data.inventorySize.ToString(CultureInfo.InvariantCulture);
                f[FRecipe] = Recipe(data.recipeIngredients);
                f[FAssoc] = data.associatedGameObject != null ? data.associatedGameObject.name : "";

                if (isCons)
                {
                    var gdc = (GroupDataConstructible)data;
                    f[FGroupCat] = EnumName(() => gdc.groupCategory);
                    f[FRotFixed] = gdc.rotationFixed ? "1" : "0";
                    f[FNextTier] = gdc.nextTierGroup != null ? gdc.nextTierGroup.id : "";
                }
                else if (isItem)
                {
                    var gdi = (GroupDataItem)data;
                    f[FItemCat] = EnumName(() => gdi.itemCategory);
                    f[FItemSub] = EnumName(() => gdi.itemSubCategory);
                    f[FEquip] = EnumName(() => gdi.equipableType);
                    f[FUsable] = EnumName(() => gdi.usableType);

                    if (gdi.craftableInList == null)
                    {
                        f[FCraftable] = "null";
                        result.NullFindings.Add(NullCraftable(id));
                    }
                    else
                    {
                        f[FCraftable] = CraftableList(gdi.craftableInList);
                    }
                }

                result.Groups.Add(row);
            }

            result.Groups.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
            return result;
        }

        private static DriftFinding NullCraftable(string id)
            => new DriftFinding
            {
                Kind = DriftKind.NullCraftableInList,
                Severity = Severity.High,
                Member = id,
                Detail = "Group '" + id + "' has a null craftableInList. GroupItem.CanBeCraftedIn() "
                       + "calls .Contains() on it with no null guard, and UiWindowCraft.CreateGrid() "
                       + "runs that over every group whenever any crafter screen opens - so this one "
                       + "group takes down every crafter until reload. Almost always a mod's group, "
                       + "not the game's.",
            };

        // ── diff ───────────────────────────────────────────────────────────────────────

        public static List<DriftFinding> Diff(
            ContentBaseline baseline, List<ContentGroupRow> current, bool rosterChanged)
        {
            var findings = new List<DriftFinding>();

            var baseById = new Dictionary<string, ContentGroupRow>(StringComparer.Ordinal);
            foreach (var g in baseline.Groups) baseById[g.Id] = g;
            var curById = new Dictionary<string, ContentGroupRow>(StringComparer.Ordinal);
            foreach (var g in current) curById[g.Id] = g;

            var attrib = rosterChanged
                ? " (plugin roster changed since the baseline, so ownership cannot be cleanly attributed)"
                : "";

            foreach (var g in current)
            {
                if (!baseById.ContainsKey(g.Id))
                    findings.Add(new DriftFinding
                    {
                        Kind = DriftKind.GroupAdded,
                        Severity = Severity.Low,
                        Member = g.Id,
                        Detail = "New " + g.ConcreteType + " group since the baseline" + attrib + ".",
                    });
            }

            foreach (var g in baseline.Groups)
            {
                if (curById.ContainsKey(g.Id)) continue;
                findings.Add(new DriftFinding
                {
                    Kind = DriftKind.GroupRemoved,
                    Severity = Severity.Medium,
                    Member = g.Id,
                    Detail = "A group present at baseline is gone. Mods that reference it by id will "
                           + "fail to resolve it" + attrib + ".",
                });
            }

            foreach (var g in current)
            {
                if (!baseById.TryGetValue(g.Id, out var b)) continue;

                if (!string.Equals(g.ConcreteType, b.ConcreteType, StringComparison.Ordinal))
                {
                    findings.Add(new DriftFinding
                    {
                        Kind = DriftKind.GroupFieldChanged,
                        Severity = Severity.Low,
                        Member = g.Id,
                        Detail = "Recategorised from " + b.ConcreteType + " to " + g.ConcreteType
                               + " - a mod keying off the group kind changes behaviour silently.",
                    });
                    continue;   // field-level diff would be all noise once the kind flipped
                }

                foreach (var kv in g.Fields)
                {
                    b.Fields.TryGetValue(kv.Key, out var oldVal);
                    oldVal ??= "";
                    if (!string.Equals(kv.Value, oldVal, StringComparison.Ordinal))
                    {
                        findings.Add(new DriftFinding
                        {
                            Kind = DriftKind.GroupFieldChanged,
                            Severity = Severity.Low,
                            Member = g.Id,
                            Detail = kv.Key + " changed: '" + oldVal + "' -> '" + kv.Value + "'.",
                        });
                    }
                }
            }

            return findings;
        }

        // ── helpers ────────────────────────────────────────────────────────────────────

        private static string Recipe(List<GroupDataItem> ingredients)
        {
            if (ingredients == null || ingredients.Count == 0) return "";

            // Aggregate counts by id, preserving first-seen order, so "Iron, Iron, Fabric" is
            // recorded as "Iron:2,Fabric:1".
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var ing in ingredients)
            {
                if (ing == null || string.IsNullOrEmpty(ing.id)) continue;
                if (!counts.TryGetValue(ing.id, out var n)) { order.Add(ing.id); counts[ing.id] = 1; }
                else counts[ing.id] = n + 1;
            }

            var sb = new StringBuilder(64);
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(order[i]).Append(':').Append(counts[order[i]].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string CraftableList(List<DataConfig.CraftableIn> list)
        {
            if (list.Count == 0) return "[]";
            var sb = new StringBuilder(48);
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(list[i].ToString());
            }
            return sb.ToString();
        }

        private static string EnumName<T>(Func<T> get)
        {
            try { var v = get(); return v != null ? v.ToString() : ""; }
            catch { return ""; }
        }
    }
}
