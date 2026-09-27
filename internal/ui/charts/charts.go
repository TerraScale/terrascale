// Package charts prepares accessible, server-rendered SVG chart geometry.
package charts

import (
	"fmt"
	"math"
	"strconv"
	"strings"
	"unicode/utf8"
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

// ChartColumn is the hover and focus target for one data point: the guide
// line, the series dots, and a tooltip listing every value, all without
// scripts. Label is the accessible name, read when the column takes focus.
type ChartColumn struct {
	X, Left, Width string
	Label          string
	Dots           []ChartDot
	Tip            ChartTip
}

// ChartTip is the tooltip beside a hover column: the point label and one row
// per series. It is placed in viewBox units, so it needs no inline style and
// stays inside the chart instead of being clipped by a scrolling container.
type ChartTip struct {
	X, Y, Width, Height string
	// SwatchX, NameX, and ValueX are the row columns; values end at ValueX.
	SwatchX, NameX, ValueX   string
	Header, HeaderX, HeaderY string
	Rows                     []ChartTipRow
}

// ChartTipRow is one series value inside a ChartTip.
type ChartTipRow struct {
	Name, Value string
	Class       string
	Y           string
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
// ticks label the five x-axis ticks; points, when given, name every sample in
// its tooltip.
func NewLine(label string, ticks, points []string, height int, series ...Series) LineData {
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
		if step < len(ticks) {
			label = ticks[step]
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
		header := ""
		if i < len(points) {
			header = points[i]
		}
		rows := make([]ChartTipRow, 0, len(series))
		values := make([]string, 0, len(series))
		for index, item := range series {
			row := ChartTipRow{Name: item.Name, Value: grouped(item.Values[i]), Class: data.Paths[index].Class}
			rows = append(rows, row)
			values = append(values, row.Name+" "+row.Value)
			column.Dots = append(column.Dots, ChartDot{X: coordinate(x), Y: coordinate(yAt(item.Values[i])), Class: row.Class})
		}
		column.Label = strings.Join(values, ", ")
		if header != "" {
			column.Label = header + ": " + column.Label
		}
		column.Tip = newTip(x, float64(top), float64(width-right), header, rows)
		data.Columns = append(data.Columns, column)
	}
	return data
}

// maxColumns bounds the hover targets rendered per chart.
const maxColumns = 60

// Keyboard behaviour of a line chart's columns, as Datastar expressions on
// the chart. The page renders every column focusable; once Datastar runs the
// chart is one tab stop, initially the latest point. The arrow keys step
// between points, Home and End jump to the ends, and the tab stop moves with
// them. The chart must not listen for focus events: Chromium makes an SVG
// element with focus listeners a tab stop of its own.
const (
	columnTabStop = `el.querySelectorAll('.ts-chart-column').forEach((column, i, all) => column.setAttribute('tabindex', i === all.length - 1 ? '0' : '-1'))`
	columnKeys    = `evt.target.matches('.ts-chart-column') && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(evt.key) && (evt.preventDefault(), [{ArrowLeft: evt.target.previousElementSibling, ArrowRight: evt.target.nextElementSibling, Home: el.querySelector('.ts-chart-column'), End: el.querySelector('.ts-chart-column:last-of-type')}[evt.key]?.closest('.ts-chart-column')].filter(Boolean).forEach(next => (evt.target.setAttribute('tabindex', '-1'), next.setAttribute('tabindex', '0'), next.focus())))`
)

// Tooltip metrics in viewBox units. The text is monospaced, so its width is
// the character count times a fixed advance.
const (
	tipCharWidth = 6.8
	tipPadX      = 10
	tipPadY      = 6
	tipLine      = 17
	tipSwatch    = 12
	tipColumnGap = 16
	tipOffset    = 10
)

// newTip sizes the tooltip to its text and places it right of the guide line
// at x, flipping left when it would cross the plot end.
func newTip(x, top, end float64, header string, rows []ChartTipRow) ChartTip {
	var names, values int
	for _, row := range rows {
		names = max(names, utf8.RuneCountInString(row.Name))
		values = max(values, utf8.RuneCountInString(row.Value))
	}
	content := max(float64(utf8.RuneCountInString(header))*tipCharWidth, tipSwatch+float64(names+values)*tipCharWidth+tipColumnGap)
	width := content + 2*tipPadX
	lines := len(rows)
	if header != "" {
		lines++
	}
	left := x + tipOffset
	if left+width > end {
		left = max(0, x-tipOffset-width)
	}
	tip := ChartTip{
		X: coordinate(left), Y: coordinate(top), Width: coordinate(width), Height: coordinate(float64(2*tipPadY + lines*tipLine)),
		SwatchX: coordinate(left + tipPadX + 3), NameX: coordinate(left + tipPadX + tipSwatch), ValueX: coordinate(left + width - tipPadX),
		Header: header, HeaderX: coordinate(left + tipPadX),
	}
	lineY := func(line int) string { return coordinate(top + tipPadY + tipLine*(float64(line)+0.5)) }
	first := 0
	if header != "" {
		tip.HeaderY, first = lineY(0), 1
	}
	for index, row := range rows {
		row.Y = lineY(first + index)
		tip.Rows = append(tip.Rows, row)
	}
	return tip
}

type RingData struct {
	Name    string
	Value   string
	Label   string
	Tone    string
	Dash    string
	Percent int
}

type BarData struct {
	Name  string
	Value string
	Label string
	Width string
}

// NewBar keeps progress geometry in SVG attributes, which work with a strict
// style-src CSP and remain readable without scripts. The tooltip shows name
// and percent; of completes the accessible label ("name: n percent of …").
func NewBar(name string, percent int, of string) BarData {
	return BarData{
		Name: name, Value: strconv.Itoa(percent) + "%",
		Label: fmt.Sprintf("%s: %d percent of %s", name, percent, of),
		Width: strconv.Itoa(min(100, max(0, percent))),
	}
}

func NewRing(label string, used, capacity int64) RingData {
	if capacity <= 0 {
		return RingData{Name: label, Value: "Capacity unavailable", Label: label + ": capacity unavailable", Tone: "success", Dash: "0 100"}
	}
	percentage := int(math.Round(float64(used) / float64(capacity) * 100))
	tone := "success"
	if percentage >= 100 {
		tone = "danger"
	} else if percentage >= 80 {
		tone = "warning"
	}
	return RingData{
		Name: label, Value: strconv.Itoa(percentage) + "%",
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

// grouped writes the exact value with thousands separators.
func grouped(value int64) string {
	digits := strconv.FormatInt(value, 10)
	sign := ""
	if value < 0 {
		sign, digits = "-", digits[1:]
	}
	var out strings.Builder
	for i, digit := range digits {
		if i > 0 && (len(digits)-i)%3 == 0 {
			out.WriteByte(',')
		}
		out.WriteRune(digit)
	}
	return sign + out.String()
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
