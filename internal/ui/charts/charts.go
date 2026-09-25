// Package charts prepares accessible, server-rendered SVG chart geometry.
package charts

import (
	"fmt"
	"math"
	"strconv"
	"strings"
)

type Series struct {
	Name   string
	Values []int64
}

type AxisLine struct {
	Y     string
	Label string
}

type AxisTick struct {
	X      string
	Label  string
	Anchor string
}

type LinePath struct {
	Name  string
	Class string
	Data  string
}

type LineData struct {
	Label    string
	ViewBox  string
	Width    int
	Height   int
	PlotLeft int
	PlotEnd  int
	Grid     []AxisLine
	Ticks    []AxisTick
	Paths    []LinePath
	AreaPath string
}

// NewLine draws zero-based values and returns an empty view for inconsistent
// series lengths, so chart paths cannot silently shift relative to each other.
func NewLine(label string, rangeLabels []string, height int, series ...Series) LineData {
	const width, left, right, top, bottom = 720, 48, 12, 14, 32
	data := LineData{
		Label: label, ViewBox: fmt.Sprintf("0 0 %d %d", width, height),
		Width: width, Height: height, PlotLeft: left, PlotEnd: width - right,
	}
	if height < 120 || len(series) == 0 || len(series[0].Values) == 0 {
		return data
	}
	count := len(series[0].Values)
	for _, item := range series {
		if len(item.Values) != count {
			return data
		}
	}
	var maximum int64
	for _, item := range series {
		for _, value := range item.Values {
			maximum = max(maximum, value)
		}
	}
	scale := niceCeiling(maximum)
	plotWidth := width - left - right
	plotHeight := height - top - bottom
	for step := range 5 {
		y := top + float64(plotHeight)*float64(step)/4
		axisLabel := ""
		if maximum > 0 {
			axisLabel = compact(scale * int64(4-step) / 4)
		} else if step == 4 {
			axisLabel = "0"
		}
		data.Grid = append(data.Grid, AxisLine{Y: coordinate(y), Label: axisLabel})
	}
	for step := range 5 {
		index := 0
		if count > 1 {
			index = int(math.Round(float64(step) * float64(count-1) / 4))
		}
		label := ""
		if step < len(rangeLabels) {
			label = rangeLabels[step]
		}
		anchor := "middle"
		if step == 0 {
			anchor = "start"
		} else if step == 4 {
			anchor = "end"
		}
		x := left + float64(plotWidth)*float64(index)/float64(max(1, count-1))
		data.Ticks = append(data.Ticks, AxisTick{X: coordinate(x), Label: label, Anchor: anchor})
	}
	for index, item := range series {
		var path strings.Builder
		for i, value := range item.Values {
			x := left + float64(plotWidth)*float64(i)/float64(max(1, count-1))
			y := top + float64(plotHeight)*(1-float64(max(0, value))/float64(scale))
			if i == 0 {
				path.WriteString("M ")
			} else {
				path.WriteString(" L ")
			}
			fmt.Fprintf(&path, "%s,%s", coordinate(x), coordinate(y))
		}
		class := fmt.Sprintf("ts-cat-%d", index+1)
		if len(series) == 1 {
			class = "ts-chart-series-1"
			data.AreaPath = fmt.Sprintf("M %d,%d L %s L %d,%d Z", left, top+plotHeight, strings.TrimPrefix(path.String(), "M "), width-right, top+plotHeight)
		}
		data.Paths = append(data.Paths, LinePath{Name: item.Name, Class: class, Data: path.String()})
	}
	return data
}

type RingData struct {
	Label   string
	Tone    string
	Dash    string
	Percent int
}

type BarData struct {
	Label string
	Width string
}

// NewBar keeps progress geometry in SVG attributes, which work with a strict
// style-src CSP and remain readable without scripts.
func NewBar(label string, percent int) BarData {
	return BarData{Label: label, Width: strconv.Itoa(min(100, max(0, percent)))}
}

func NewRing(label string, used, capacity int64) RingData {
	if capacity <= 0 {
		return RingData{Label: label + ": capacity unavailable", Tone: "success", Dash: "0 100"}
	}
	percentage := int(math.Round(float64(used) / float64(capacity) * 100))
	tone := "success"
	if percentage >= 100 {
		tone = "danger"
	} else if percentage >= 80 {
		tone = "warning"
	}
	return RingData{
		Label: fmt.Sprintf("%s: %d%% of illustrative demo capacity", label, percentage),
		Tone:  tone, Dash: strconv.Itoa(min(100, max(0, percentage))) + " 100", Percent: percentage,
	}
}

func coordinate(value float64) string {
	return strconv.FormatFloat(value, 'f', 1, 64)
}

func niceCeiling(value int64) int64 {
	if value <= 0 {
		return 1
	}
	magnitude := int64(1)
	for magnitude <= value/10 {
		magnitude *= 10
	}
	for _, factor := range []int64{1, 2, 5, 10} {
		if factor*magnitude >= value {
			return factor * magnitude
		}
	}
	return 10 * magnitude
}

func compact(value int64) string {
	switch {
	case value >= 1_000_000:
		return fmt.Sprintf("%.1fM", float64(value)/1_000_000)
	case value >= 1_000:
		return fmt.Sprintf("%.1fk", float64(value)/1_000)
	default:
		return strconv.FormatInt(value, 10)
	}
}
