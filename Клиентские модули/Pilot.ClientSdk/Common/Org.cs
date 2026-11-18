using System;
using System.Collections.Generic;
using System.Linq;
using Ascon.Pilot.SDK;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Оргструктура: снимок оргединиц с обратными ссылками на родителя (в SDK их нет).
    /// Замена OrganisationUnitService / OrganisationUnitWrapper из NugetPackage.
    /// <code>
    /// var org = PilotApi.Org;                                  // или new Org(repository)
    /// var dept = org.DepartmentOf(person);                     // отдел основной должности
    /// var people = org.PersonIdsIn(dept.Id);                   // все люди отдела (с подотделами)
    /// var company = org.ContrAgent(person.MainPosition.Position);   // «ООО …», «ГУП …» выше по дереву
    /// </code>
    /// </summary>
    public sealed class Org
    {
        /// <summary>Префиксы названий оргединиц-контрагентов (организаций).</summary>
        public static readonly string[] DefaultContrAgentPrefixes = { "ГУП", "ООО", "ГКУ", "АО", "Метроинжпроект" };

        private readonly Dictionary<int, IOrganisationUnit> _units;
        private readonly Dictionary<int, int> _parent = new Dictionary<int, int>();

        public Org(IObjectsRepository repository) : this(repository?.GetOrganisationUnits())
        {
        }

        public Org(IEnumerable<IOrganisationUnit> units)
        {
            _units = (units ?? Enumerable.Empty<IOrganisationUnit>())
                .Where(u => u != null)
                .GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var unit in _units.Values)
                foreach (var child in unit.Children ?? Enumerable.Empty<int>())
                    _parent[child] = unit.Id;
        }

        public IReadOnlyCollection<IOrganisationUnit> All => _units.Values;

        public IOrganisationUnit Unit(int id) => _units.TryGetValue(id, out var unit) ? unit : null;

        public IOrganisationUnit Parent(int id) => _parent.TryGetValue(id, out var parent) ? Unit(parent) : null;

        /// <summary>Предки от ближайшего к корню.</summary>
        public IReadOnlyList<IOrganisationUnit> Ancestors(int id)
        {
            var result = new List<IOrganisationUnit>();
            var seen = new HashSet<int> { id };
            for (var p = Parent(id); p != null && seen.Add(p.Id); p = Parent(p.Id))
                result.Add(p);
            return result;
        }

        /// <summary>Отдел (ближайшая оргединица-не должность) для должности; для отдела - он сам.</summary>
        public IOrganisationUnit DepartmentOf(int unitId)
        {
            var unit = Unit(unitId);
            if (unit == null || !unit.IsPosition)
                return unit;
            return Ancestors(unitId).FirstOrDefault(u => !u.IsPosition);
        }

        /// <summary>Отдел основной должности человека.</summary>
        public IOrganisationUnit DepartmentOf(IPerson person) => person?.MainPosition == null ? null : DepartmentOf(person.MainPosition.Position);

        /// <summary>Должности внутри оргединицы (рекурсивно). Для должности - она сама.</summary>
        public IReadOnlyList<int> PositionIds(int unitId)
        {
            var result = new List<int>();
            foreach (var unit in SelfAndDescendants(unitId))
                if (unit.IsPosition)
                    result.Add(unit.Id);
            return result;
        }

        /// <summary>Люди на должностях внутри оргединицы (рекурсивно, без повторов). Замена GetUnitSubPersonsIDs.</summary>
        public IReadOnlyList<int> PersonIdsIn(int unitId) =>
            SelfAndDescendants(unitId)
                .Where(u => u.IsPosition && !u.IsDeleted)
                .Select(u => u.Person())
                .Where(p => p > 0)
                .Distinct()
                .ToList();

        /// <summary>Первая оргединица с таким названием (без учёта регистра и кавычек).</summary>
        public IOrganisationUnit UnitByTitle(string title)
        {
            var key = Text.CleanTitle(title);
            return _units.Values.FirstOrDefault(u => !u.IsDeleted &&
                                                     string.Equals(Text.CleanTitle(u.Title), key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Организация (контрагент) для оргединицы: ближайшая вверх по дереву, название которой начинается
        /// с одного из префиксов (<see cref="DefaultContrAgentPrefixes"/> по умолчанию).
        /// </summary>
        public IOrganisationUnit ContrAgent(int unitId, IEnumerable<string> prefixes = null)
        {
            var list = (prefixes ?? DefaultContrAgentPrefixes).ToList();
            var self = Unit(unitId);
            foreach (var unit in new[] { self }.Concat(Ancestors(unitId)))
                if (unit != null && list.Any(p => (unit.Title ?? "").StartsWith(p, StringComparison.Ordinal)))
                    return unit;
            return null;
        }

        /// <summary>Путь «Организация / Отдел / Должность».</summary>
        public string Path(int unitId, string separator = " / ")
        {
            var unit = Unit(unitId);
            if (unit == null)
                return string.Empty;
            return string.Join(separator, Ancestors(unitId).Reverse().Select(u => u.Title).Concat(new[] { unit.Title }));
        }

        private IEnumerable<IOrganisationUnit> SelfAndDescendants(int unitId)
        {
            var root = Unit(unitId);
            if (root == null)
                yield break;
            var seen = new HashSet<int>();
            var stack = new Stack<IOrganisationUnit>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var unit = stack.Pop();
                if (!seen.Add(unit.Id))
                    continue;
                yield return unit;
                foreach (var child in unit.Children ?? Enumerable.Empty<int>())
                    if (Unit(child) is IOrganisationUnit c)
                        stack.Push(c);
            }
        }
    }
}
