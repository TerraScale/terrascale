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

// ChartDot marks one series value inside a hover column.
type ChartDot struct {
	X, Y  string
	Class string
}

// ChartColumn is the hover target for one data point: the guide line, the
// series dots, and a native tooltip listing every value, all without scripts.
type ChartColumn struct {
	X, Left, Width string
	Title          string
	Dots           []ChartDot
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
	Columns  []ChartColumn
	// GradientID names the area fill gradient; unique per chart label.
	GradientID string
	PlotTop    int
	PlotBottom int
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
	data.PlotTop, data.PlotBottom = top, top+plotHeight
	data.GradientID = fmt.Sprintf("ts-chart-fill-%x", fnv32(label))
	xAt := func(i int) float64 { return left + float64(plotWidth)*float64(i)/float64(max(1, count-1)) }
	yAt := func(value int64) float64 { return top + float64(plotHeight)*(1-float64(max(0, value))/float64(scale)) }
	for index, item := range series {
		points := make([][2]float64, count)
		for i, value := range item.Values {
			points[i] = [2]float64{xAt(i), yAt(value)}
		}
		line := smoothPath(points)
		class := fmt.Sprintf("ts-cat-%d", index+1)
		if len(series) == 1 {
			class = "ts-chart-series-1"
			data.AreaPath = fmt.Sprintf("%s L %d,%d L %d,%d Z", line, width-right, top+plotHeight, left, top+plotHeight)
		}
		data.Paths = append(data.Paths, LinePath{Name: item.Name, Class: class, Data: line})
	}
	// Dense series share hover columns so the markup stays small; each
	// column reports the sample at its center.
	step := max(1, (count+maxColumns-1)/maxColumns)
	columnWidth := float64(plotWidth) * float64(step) / float64(max(1, count-1))
	for i := step / 2; i < count; i += step {
		x := xAt(i)
		column := ChartColumn{
			X:     coordinate(x),
			Left:  coordinate(max(float64(left), x-columnWidth/2)),
			Width: coordinate(min(float64(width-right), x+columnWidth/2) - max(float64(left), x-columnWidth/2)),
		}
		values := make([]string, 0, len(series))
		for index, item := range series {
			values = append(values, fmt.Sprintf("%s: %s", item.Name, compact(item.Values[i])))
			column.Dots = append(column.Dots, ChartDot{X: coordinate(x), Y: coordinate(yAt(item.Values[i])), Class: data.Paths[index].Class})
		}
		column.Title = strings.Join(values, " · ")
		data.Columns = append(data.Columns, column)
	}
	return data
}

// maxColumns bounds the hover targets rendered per chart.
const maxColumns = 60

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

// smoothPath draws a monotone cubic curve through the points, so the line
// never overshoots below zero or above a peak between samples.
func smoothPath(points [][2]float64) string {
	var path strings.Builder
	fmt.Fprintf(&path, "M %s,%s", coordinate(points[0][0]), coordinate(points[0][1]))
	n := len(points)
	if n < 3 {
		for _, point := range points[1:] {
			fmt.Fprintf(&path, " L %s,%s", coordinate(point[0]), coordinate(point[1]))
		}
		return path.String()
	}
	slopes := make([]float64, n-1)
	for i := range n - 1 {
		slopes[i] = (points[i+1][1] - points[i][1]) / (points[i+1][0] - points[i][0])
	}
	tangents := make([]float64, n)
	tangents[0], tangents[n-1] = slopes[0], slopes[n-2]
	for i := 1; i < n-1; i++ {
		if slopes[i-1]*slopes[i] > 0 {
			tangents[i] = 2 / (1/slopes[i-1] + 1/slopes[i])
		}
	}
	for i := range n - 1 {
		dx := (points[i+1][0] - points[i][0]) / 3
		fmt.Fprintf(&path, " C %s,%s %s,%s %s,%s",
			coordinate(points[i][0]+dx), coordinate(points[i][1]+tangents[i]*dx),
			coordinate(points[i+1][0]-dx), coordinate(points[i+1][1]-tangents[i+1]*dx),
			coordinate(points[i+1][0]), coordinate(points[i+1][1]))
	}
	return path.String()
}

func fnv32(value string) uint32 {
	hash := uint32(2166136261)
	for i := range len(value) {
		hash = (hash ^ uint32(value[i])) * 16777619
	}
	return hash
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
