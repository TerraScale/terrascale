using System.Text.Json;

namespace TerraScale.Dashboard.Common.UI
{
    public sealed record RegionMapField(string SelectId, string Selected = "GRU", IReadOnlyDictionary<string, string>? Pings = null, IReadOnlyList<string>? Available = null)
    {
        private string Target => JsonSerializer.Serialize(SelectId);
        public string Sync => $$"""(() => { const control = document.getElementById({{Target}}); const selected = control.tagName === 'SELECT' ? [...control.selectedOptions].map(o => o.value) : [...control.querySelectorAll('input[type=checkbox]:checked')].map(i => i.value); el.dataset.selected = selected.map(value => value.toUpperCase()).join(' '); el.querySelectorAll('button[data-region]').forEach(node => node.setAttribute('aria-pressed', String(selected.some(value => value.toUpperCase() === node.dataset.region)))) })()""";
        public string Change => $"document.getElementById({Target}).contains(evt.target) && ({Sync})";
        public string Pick => $$"""(() => { const control = document.getElementById({{Target}}); if (control.tagName === 'SELECT') { const option = [...control.options].find(o => o.value.toUpperCase() === el.dataset.region); if (!option || option.disabled) return; if (control.multiple) option.selected = !option.selected; else control.value = option.value; control.dispatchEvent(new Event('change', { bubbles: true })); } else { const input = [...control.querySelectorAll('input[type=checkbox]')].find(i => i.value.toUpperCase() === el.dataset.region); if (!input || input.disabled) return; input.checked = !input.checked; input.dispatchEvent(new Event('change', { bubbles: true })); } })()""";
        public bool Shows(string region)
        {
            return Available is null || Available.Contains(region, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsSelected(string region)
        {
            return Selected.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(region, StringComparer.OrdinalIgnoreCase);
        }

        public string Visible => string.Join(' ', Selected.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(Shows).Select(code => code.ToUpperInvariant()));
        public string? Ping(string region)
        {
            return Pings?.GetValueOrDefault(region);
        }
    }
}
