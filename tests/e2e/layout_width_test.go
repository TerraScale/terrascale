package e2e

import (
	"encoding/json"
	"fmt"
	"net/http"
	"slices"
	"testing"
)

// Keep the page layout within the actual content column, not just the viewport.
func TestPageLayoutsFitContentColumn(t *testing.T) {
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)

	routes := []routeState{{key: "billing", path: "/billing"}}
	for _, route := range parityRouteStates {
		if route.anonymous || route.status != 0 || slices.Contains(publicPaths, route.path) {
			continue
		}
		routes = append(routes, route)
	}

	for _, width := range []int{360, 768, 1024, 1100, 1180, 1440} {
		must(t, page.SetViewportSize(width, 900))
		for _, route := range routes {
			t.Run(fmt.Sprintf("%s/%d", route.key, width), func(t *testing.T) {
				response := goTo(t, page, route.path)
				if response.Status() != http.StatusOK {
					t.Fatalf("GET %s = %d, want 200", route.path, response.Status())
				}
				if layoutScreenshot(route.key, width) {
					viewportScreenshot(t, page, fmt.Sprintf("layout/%s-%d", route.key, width))
				}
				var result struct {
					ColumnWidth float64  `json:"columnWidth"`
					Problems    []string `json:"problems"`
				}
				raw, err := page.Evaluate(`() => {
					const main = document.querySelector('.ts-main');
					const columnWidth = main.getBoundingClientRect().width;
					const problems = [];
					const visible = el => el.getClientRects().length > 0;
					const box = el => el.getBoundingClientRect();
					const inside = (child, parent) => box(child).right <= box(parent).right + 1;
					if (getComputedStyle(main).containerName !== 'page')
						problems.push('.ts-main is not the named page container');
					if (document.documentElement.scrollWidth > innerWidth + 1)
						problems.push('horizontal page scroll: ' + document.documentElement.scrollWidth + ' > ' + innerWidth);
					if (location.pathname === '/audit' && innerWidth === 360) {
						const header = document.querySelector('#recent-activity .ts-card-head');
						const title = header?.querySelector('.ts-card-title');
						const description = header?.querySelector('.ts-card-description');
						if (!title || !description || header?.querySelector('.ts-card-actions'))
							problems.push('audit card head must hold only the title and description');
						const filters = document.querySelector('#recent-activity .ts-audit-filters');
						const apply = filters?.querySelector('.ts-audit-apply');
						const exportMenu = filters?.querySelector('.ts-menu > summary');
						if (!apply || !exportMenu ||
							box(exportMenu).top < box(apply).top - 1 || box(exportMenu).bottom > box(apply).bottom + 1)
							problems.push('audit export must sit next to Apply filters in the filter bar');
					}
					for (const [selector, breakpoint] of [['.ts-grid-2', 768], ['.ts-grid-3', 960], ['.ts-split', 960]]) {
						for (const grid of document.querySelectorAll(selector)) {
							if (!visible(grid)) continue;
							const columns = getComputedStyle(grid).gridTemplateColumns.split(/\s+/).length;
							const expected = columnWidth < breakpoint ? 1 : (selector === '.ts-grid-3' ? 3 : 2);
							if (columns !== expected) problems.push(selector + ' has ' + columns + ' columns, want ' + expected);
						}
					}
					if (location.pathname === '/access' && innerWidth === 1440) {
						const nested = document.createElement('div');
						nested.style.width = '600px';
						nested.style.containerType = 'inline-size';
						nested.innerHTML = '<div class="ts-grid-2"><span>A</span><span>B</span></div>';
						main.append(nested);
						const columns = getComputedStyle(nested.firstElementChild).gridTemplateColumns.split(/\s+/).length;
						nested.remove();
						if (columns !== 2) problems.push('nested container overrides the page breakpoint');
					}
					for (const text of document.querySelectorAll('.ts-list-main > .ts-truncate')) {
						if (visible(text) && (!inside(text, text.parentElement) || !inside(text, text.closest('.ts-list-item, .ts-list') || text.parentElement)))
							problems.push('list text escapes its row: ' + text.textContent.trim());
					}
					if (location.pathname === '/access' && innerWidth === 360) {
						const email = [...document.querySelectorAll('.ts-list-main > .ts-truncate')]
							.find(el => el.textContent.trim() === 'mario@terrascale.tech');
						if (!email || email.scrollWidth <= email.clientWidth)
							problems.push('long member email is not truncated');
						const list = document.createElement('ul');
						list.className = 'ts-list';
						list.style.width = '180px';
						list.innerHTML = '<li class="ts-list-item"><div class="ts-list-main"><code class="ts-mono">prj_abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz</code></div></li>';
						main.append(list);
						const code = list.querySelector('code');
						if (!inside(code, list) || code.scrollWidth > code.clientWidth + 1)
							problems.push('unbroken list value escapes its row');
						list.remove();
					}
					for (const row of document.querySelectorAll('.ts-idp-copy')) {
						if (!visible(row)) continue;
						const value = row.querySelector('.ts-idp-copy-value');
						const button = row.querySelector('button');
						if (!inside(value, row) || !inside(button, row) || box(value).right > box(button).left + 1)
							problems.push('copyable value escapes its row: ' + value.textContent.trim());
					}
					for (const value of document.querySelectorAll('.ts-stat-value')) {
						if (visible(value) && (value.scrollWidth > value.clientWidth + 1 || value.scrollHeight > value.clientHeight + 1 || !inside(value, value.closest('.ts-stats') || value.parentElement)))
							problems.push('stat is clipped: ' + value.textContent.trim());
					}
					return JSON.stringify({columnWidth, problems});
				}`)
				must(t, err)
				must(t, json.Unmarshal([]byte(raw.(string)), &result))
				for _, problem := range result.Problems {
					t.Errorf("%s at %dpx (content column %.0fpx): %s", route.path, width, result.ColumnWidth, problem)
				}
			})
		}
	}
}

func layoutScreenshot(route string, width int) bool {
	switch route {
	case "team", "profile":
		return width == 768
	case "overview", "usage":
		return width == 1100 || (route == "usage" && width == 360)
	case "auth-branding", "auth-sessions":
		return width == 360 || width == 768
	}
	return false
}
