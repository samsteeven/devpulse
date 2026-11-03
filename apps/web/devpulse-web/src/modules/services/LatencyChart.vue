<script setup lang="ts">
import { LineChart, ScatterChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import * as echarts from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { MetricsPoint } from '@/types/api'

echarts.use([LineChart, ScatterChart, GridComponent, TooltipComponent, CanvasRenderer])

const props = defineProps<{ points: MetricsPoint[] }>()

const element = ref<HTMLDivElement>()
let chart: echarts.ECharts | undefined
let resizeObserver: ResizeObserver | undefined

function render() {
  const answered = props.points.filter((p) => p.checks > p.failures)
  const failures = props.points.filter((p) => p.failures > 0)

  chart?.setOption({
    animation: false,
    grid: { left: 56, right: 16, top: 16, bottom: 32 },
    tooltip: {
      trigger: 'axis',
      backgroundColor: '#1d2530',
      borderColor: '#2a3441',
      textStyle: { color: '#e6edf3', fontSize: 12 },
    },
    xAxis: {
      type: 'time',
      axisLine: { lineStyle: { color: '#2a3441' } },
      axisLabel: { color: '#9aa7b4', hideOverlap: true },
      splitLine: { show: false },
    },
    yAxis: {
      type: 'value',
      min: 0,
      axisLabel: { color: '#9aa7b4', formatter: (value: number) => `${value.toLocaleString('fr-FR')} ms` },
      splitLine: { lineStyle: { color: '#2a3441' } },
    },
    series: [
      {
        name: 'Temps de réponse moyen',
        type: 'line',
        showSymbol: answered.length < 40,
        symbolSize: 6,
        lineStyle: { color: '#4c9eff', width: 2 },
        itemStyle: { color: '#4c9eff' },
        data: answered.map((p) => [p.bucketStart, p.averageResponseTimeMs]),
      },
      {
        name: 'Échecs',
        type: 'scatter',
        symbol: 'rect',
        symbolSize: 9,
        itemStyle: { color: '#f85149' },
        data: failures.map((p) => [p.bucketStart, 0, p.failures]),
        tooltip: { valueFormatter: () => 'échec' },
      },
    ],
  }, { notMerge: true })
}

onMounted(() => {
  chart = echarts.init(element.value!)
  render()
  resizeObserver = new ResizeObserver(() => chart?.resize())
  resizeObserver.observe(element.value!)
})

watch(() => props.points, render)

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  chart?.dispose()
})
</script>

<template>
  <div ref="element" class="h-64 w-full" role="img" aria-label="Temps de réponse moyen et échecs sur la période" />
</template>
