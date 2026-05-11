let dungeonChart = null;

window.renderDungeonChart = (labels, data) => {
    const ctx = document.getElementById('xpChart');
    if (!ctx) return;

    if (dungeonChart) {
        dungeonChart.destroy();
        dungeonChart = null;
    }

    dungeonChart = new Chart(ctx, {
        type: 'bar',
        data: {
            labels: labels,
            datasets: [{
                label: 'XP',
                data: data,
                backgroundColor: labels.map((_, i) =>
                    `hsla(${5 + i * 18}, 60%, 38%, 0.8)`),
                borderColor: labels.map((_, i) =>
                    `hsl(${5 + i * 18}, 60%, 30%)`),
                borderWidth: 1,
                borderRadius: 4,
            }]
        },
        options: {
            // Canvas is inside a fixed-height div so we must opt out of
            // aspect ratio and let CSS drive the height.
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                y: {
                    beginAtZero: true,
                    grid: { color: 'rgba(140,115,97,0.12)' },
                    ticks: { color: '#8c7361', font: { size: 11 } }
                },
                x: {
                    grid: { display: false },
                    ticks: { color: '#5e4634', font: { size: 11 } }
                }
            },
            plugins: {
                legend: { display: false },
                tooltip: {
                    callbacks: {
                        label: ctx => ` ${ctx.parsed.y.toLocaleString()} XP`
                    }
                }
            }
        }
    });
};
