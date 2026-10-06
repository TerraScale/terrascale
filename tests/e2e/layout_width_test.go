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

	for _, width := range []int{360, 768, 1024, 1100, 1180, 1280, 1440} {
		must(t, page.SetViewportSize(width, 900))
		for _, route := range routes {
			if width == 1280 && route.key != "audit" {
				continue
			}
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
					if (location.pathname === '/organization' && new URLSearchParams(location.search).get('tab') === 'audit') {
						const card = document.querySelector('#recent-activity');
						const header = card?.querySelector('.ts-card-head');
						if (!header?.querySelector('.ts-card-title') || !header?.querySelector('.ts-card-description') ||
							header.querySelector('.ts-card-actions'))
							problems.push('audit card head must hold only the title and description');
						const filters = card?.querySelector('.ts-audit-filters');
						const controls = filters ? [...filters.children].filter(el => el.type !== 'hidden') : [];
						const order = controls.map(el => el.htmlFor || (el.matches('.ts-audit-apply') ? 'apply' : 'export'));
						if (order.join(',') !== 'audit-search,audit-action,audit-severity,audit-range,apply,export' ||
							!controls[5]?.matches('.ts-menu') || card?.querySelectorAll('.ts-menu').length !== 1)
							problems.push('audit filters must end with Apply filters and one Export menu');
						for (const control of controls) {
							if (box(control).left < box(filters).left - 1 || !inside(control, filters))
								problems.push('audit filter escapes its row');
						}
						if (innerWidth >= 1280 && controls.length === 6) {
							const bottom = box(controls[0]).bottom;
							if (controls.some(el => Math.abs(box(el).bottom - bottom) > 1))
								problems.push('audit filters and buttons must share one aligned desktop row');
							if (controls.some((el, index) => index > 0 && box(el).left < box(controls[index - 1]).right - 1))
								problems.push('audit filter controls overlap or are out of order');
						}
					}
					for (const [selector, breakpoint] of [['.ts-grid-2', 768], ['.ts-grid-3', 960], ['.ts-split', 960]]) {
						for (const grid of document.querySelectorAll(selector)) {
							if (!visible(grid)) continue;
							const columns = getComputedStyle(grid).gridTemplateColumns.split(/\s+/).length;
							const expected = columnWidth < breakpoint ? 1 : (selector === '.ts-grid-3' ? 3 : 2);
							if (columns !== expected) problems.push(selector + ' has ' + columns + ' columns, want ' + expected);
						}
					}
					for (const text of document.querySelectorAll('.ts-list-main > .ts-truncate')) {
						if (visible(text) && (!inside(text, text.parentElement) || !inside(text, text.closest('.ts-list-item, .ts-list') || text.parentElement)))
							problems.push('list text escapes its row: ' + text.textContent.trim());
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
	case "audit":
		return width == 360 || width == 1280 || width == 1440
	case "team":
		return width == 360 || width == 768 || width == 1440
	case "profile":
		return width == 768
	case "overview", "usage":
		return width == 1100 || (route == "usage" && width == 360)
	case "auth-branding", "auth-sessions":
		return width == 360 || width == 768
	}
	return false
}
