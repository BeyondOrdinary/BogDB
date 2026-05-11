// Chart.js helpers for SocialGraph · BogDB
window.createBarChart = function (id, labels, values, colors) {
    const ctx = document.getElementById(id);
    if (!ctx) return;
    if (ctx._c) ctx._c.destroy();
    ctx._c = new Chart(ctx, {
        type: 'bar',
        data: {
            labels,
            datasets: [{ data: values,
                backgroundColor: labels.map((_, i) => (colors[i % colors.length]) + 'cc'),
                borderColor:     labels.map((_, i) => colors[i % colors.length]),
                borderWidth: 1, borderRadius: 4 }]
        },
        options: {
            responsive: true, maintainAspectRatio: false,
            plugins: { legend: { display: false } },
            scales: {
                x: { ticks: { color: '#9d95c9', font: { size: 11 } }, grid: { color: '#232750' } },
                y: { ticks: { color: '#9d95c9', font: { size: 11 } }, grid: { color: '#232750' } }
            }
        }
    });
};

window.createDoughnutChart = function (id, labels, values, colors) {
    const ctx = document.getElementById(id);
    if (!ctx) return;
    if (ctx._c) ctx._c.destroy();
    ctx._c = new Chart(ctx, {
        type: 'doughnut',
        data: { labels, datasets: [{ data: values,
            backgroundColor: colors.map(c => c + 'bb'),
            borderColor: colors, borderWidth: 2 }] },
        options: {
            responsive: true, maintainAspectRatio: false,
            plugins: { legend: { position: 'right',
                labels: { color: '#9d95c9', font: { size: 11 }, padding: 12 } } }
        }
    });
};
