// charts.js — Chart.js 4 helpers for PackageEcosystem

window.renderBarChart = (canvasId, labels, values, label, color) => {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const existing = Chart.getChart(canvasId);
    if (existing) existing.destroy();

    const ctx = canvas.getContext('2d');
    new Chart(ctx, {
        type: 'bar',
        data: {
            labels,
            datasets: [{
                label,
                data: values,
                backgroundColor: color + '55',
                borderColor: color,
                borderWidth: 2,
                borderRadius: 6,
            }]
        },
        options: {
            responsive: true,
            plugins: {
                legend: { labels: { color: '#94a3b8', font: { family: 'Inter' } } },
            },
            scales: {
                x: { ticks: { color: '#64748b' }, grid: { color: 'rgba(255,255,255,0.05)' } },
                y: { ticks: { color: '#64748b' }, grid: { color: 'rgba(255,255,255,0.05)' }, beginAtZero: true }
            }
        }
    });
};

window.renderDoughnutChart = (canvasId, labels, values, colors) => {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const existing = Chart.getChart(canvasId);
    if (existing) existing.destroy();

    const ctx = canvas.getContext('2d');
    new Chart(ctx, {
        type: 'doughnut',
        data: {
            labels,
            datasets: [{
                data: values,
                backgroundColor: colors ?? ['#818cf8','#a78bfa','#2dd4bf','#fbbf24','#f87171','#fb923c'],
                borderWidth: 0,
                hoverOffset: 6,
            }]
        },
        options: {
            responsive: true,
            cutout: '65%',
            plugins: {
                legend: { position: 'right', labels: { color: '#94a3b8', font: { family: 'Inter' } } }
            }
        }
    });
};
