/* clayo-page.js: charts, diagrams, sortable tables, decks and links for Clayo pages (format 1). */
(function () {
  "use strict";

  var me = document.currentScript;
  var base = me && me.src ? me.src.replace(/[^\/]*$/, "") : "";
  var embedded = window.parent !== window;
  var root = document.documentElement;

  function each(sel, fn) { Array.prototype.forEach.call(document.querySelectorAll(sel), fn); }

  // Chart.js and Mermaid sit beside this file; in a saved copy they are already in the page.
  function load(file, there) {
    if (there()) return Promise.resolve();
    return new Promise(function (ok, fail) {
      var s = document.createElement("script");
      s.src = base + file;
      s.onload = ok;
      s.onerror = function () { fail(new Error("couldn't load " + file)); };
      document.head.appendChild(s);
    });
  }

  // A colour token as the browser resolves it in the current theme, as rgb(r, g, b).
  var probe;
  function token(name) {
    if (!probe) {
      probe = document.createElement("i");
      probe.style.display = "none";
      document.body.appendChild(probe);
    }
    probe.style.color = "var(" + name + ")";
    return getComputedStyle(probe).color;
  }
  function alpha(rgb, a) { return rgb.replace("rgb(", "rgba(").replace(")", ", " + a + ")"); }

  function fail(el, what, e) {
    console.error("clayo-page: " + what + ": " + e.message);
    var box = document.createElement("div");
    box.className = "callout bad";
    box.textContent = what + ": " + e.message;
    el.replaceWith(box);
  }

  // ---- Charts: <script type="application/json" class="chart"> ----

  var charts = [];
  var SHAPES = ["circle", "rectRot", "triangle", "rect", "rectRounded", "circle"];

  function makeChart(c) {
    var s = c.spec, type = s.type || "bar";
    var round = type === "pie" || type === "doughnut";
    var text = token("--muted"), line = token("--border");
    var cols = [1, 2, 3, 4, 5, 6].map(function (i) { return token("--c" + i); });
    var series = s.series || [];

    var datasets = series.map(function (d, i) {
      var col = cols[i % 6];
      return {
        label: d.name,
        data: type === "scatter"
          ? d.data.map(function (p) { return Array.isArray(p) ? { x: p[0], y: p[1] } : p; })
          : d.data,
        backgroundColor: round ? d.data.map(function (_, j) { return cols[j % 6]; })
          : type === "area" ? alpha(col, 0.18) : col,
        borderColor: round ? token("--surface") : col,
        borderWidth: type === "bar" ? 0 : 2,
        borderRadius: 3,
        maxBarThickness: 64,
        fill: type === "area",
        tension: 0.3,
        pointStyle: SHAPES[i % SHAPES.length],
        pointRadius: type === "scatter" ? 4 : 3
      };
    });

    function axis(label) {
      return {
        stacked: !!s.stacked,
        title: { display: !!label, text: label, color: text },
        ticks: { color: text },
        grid: { color: line },
        border: { color: line }
      };
    }

    return new Chart(c.canvas, {
      type: type === "area" ? "line" : type,
      data: { labels: s.labels, datasets: datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        indexAxis: s.horizontal ? "y" : "x",
        plugins: {
          legend: {
            display: round || series.length > 1,
            position: "bottom",
            labels: { color: text, usePointStyle: true, boxHeight: 7 }
          }
        },
        scales: round ? {} : { x: axis(s.xLabel), y: axis(s.yLabel) }
      }
    });
  }

  function drawCharts() {
    charts.forEach(function (c) {
      if (c.chart) c.chart.destroy();
      c.chart = makeChart(c);
    });
  }

  function setupCharts() {
    each("script.chart", function (el) {
      var spec;
      try { spec = JSON.parse(el.textContent); }
      catch (e) { fail(el, "Chart data is not valid JSON", e); return; }

      var fig = document.createElement("figure");
      fig.className = "chart";
      if (spec.title) fig.appendChild(document.createElement("figcaption")).textContent = spec.title;
      var box = fig.appendChild(document.createElement("div"));
      box.className = "chart-box";
      var canvas = box.appendChild(document.createElement("canvas"));
      canvas.setAttribute("role", "img");
      canvas.setAttribute("aria-label", spec.title || "Chart");
      el.replaceWith(fig);
      charts.push({ spec: spec, canvas: canvas, fig: fig });
    });
    if (!charts.length) return Promise.resolve();

    return load("chart.umd.min.js", function () { return !!window.Chart; }).then(function () {
      Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
      Chart.defaults.font.size = 12;
      try { drawCharts(); }
      catch (e) { charts.forEach(function (c) { if (!c.chart) fail(c.fig, "Chart couldn't be drawn", e); }); }
    });
  }

  // ---- Diagrams: <pre class="mermaid"> ----

  var diagrams = [];

  function drawDiagrams() {
    if (!diagrams.length) return Promise.resolve();
    var dark = /^rgb\((\d+), (\d+), (\d+)/.exec(token("--bg"));
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: "strict",
      theme: "base",
      themeVariables: {
        darkMode: !!dark && +dark[1] + +dark[2] + +dark[3] < 382,
        fontFamily: getComputedStyle(document.body).fontFamily,
        fontSize: "14px",
        background: token("--surface"),
        primaryColor: token("--surface-2"),
        primaryTextColor: token("--text"),
        primaryBorderColor: token("--muted"),
        lineColor: token("--muted"),
        edgeLabelBackground: token("--surface"),
        secondaryColor: token("--surface"),
        tertiaryColor: token("--bg"),
        noteBkgColor: token("--surface-2"),
        noteTextColor: token("--text"),
        noteBorderColor: token("--border")
      }
    });
    diagrams.forEach(function (d) {
      d.el.removeAttribute("data-processed");
      d.el.textContent = d.src;
    });
    return mermaid.run({ nodes: diagrams.map(function (d) { return d.el; }) }).catch(function (e) {
      console.error("clayo-page: diagram: " + (e && e.message || e));
    });
  }

  function setupDiagrams() {
    each("pre.mermaid", function (el) { diagrams.push({ el: el, src: el.textContent }); });
    if (!diagrams.length) return Promise.resolve();
    return load("mermaid.min.js", function () { return !!window.mermaid; }).then(drawDiagrams);
  }

  // ---- Tables: scroll inside themselves, numbers to the right, click a header to sort ----

  function cellValue(td) {
    var raw = td.getAttribute("data-sort") || td.textContent.trim();
    var n = parseFloat(raw.replace(/[,\s%$€£]/g, ""));
    return /^[-+]?[$€£]?[\d.,\s]+\s*[%a-zA-Zµ]{0,4}$/.test(raw) && !isNaN(n) ? n : raw.toLowerCase();
  }

  function setupTables() {
    each("table.table", function (table) {
      var wrap = document.createElement("div");
      wrap.className = "table-wrap";
      table.replaceWith(wrap);
      wrap.appendChild(table);

      var body = table.tBodies[0];
      var heads = table.tHead ? table.tHead.rows[0].cells : [];
      if (!body) return;

      Array.prototype.forEach.call(heads, function (th, col) {
        var cells = Array.prototype.map.call(body.rows, function (r) { return r.cells[col]; }).filter(Boolean);
        var numeric = cells.length && cells.every(function (td) {
          return td.textContent.trim() === "" || typeof cellValue(td) === "number";
        });
        if (numeric) cells.concat(th).forEach(function (c) { c.classList.add("num"); });

        if (!table.hasAttribute("data-sortable")) return;
        th.tabIndex = 0;
        function sort() {
          var up = th.getAttribute("aria-sort") !== "ascending";
          Array.prototype.forEach.call(heads, function (h) { h.removeAttribute("aria-sort"); });
          th.setAttribute("aria-sort", up ? "ascending" : "descending");
          Array.prototype.slice.call(body.rows)
            .map(function (r) { return { r: r, v: r.cells[col] ? cellValue(r.cells[col]) : "" }; })
            .sort(function (a, b) {
              var d = typeof a.v === "number" && typeof b.v === "number"
                ? a.v - b.v : String(a.v).localeCompare(String(b.v));
              return up ? d : -d;
            })
            .forEach(function (x) { body.appendChild(x.r); });
        }
        th.addEventListener("click", sort);
        th.addEventListener("keydown", function (e) {
          if (e.key === "Enter" || e.key === " ") { e.preventDefault(); sort(); }
        });
      });
    });
  }

  // ---- Decks: ← → / Space, a counter, #3 in the address ----

  function setupDeck() {
    var deck = document.querySelector(".deck");
    if (!deck) return;
    var slides = deck.querySelectorAll(".slide");
    if (!slides.length) return;
    var at = 0;

    var nav = document.createElement("nav");
    nav.className = "deck-nav";
    nav.innerHTML = '<button type="button" aria-label="Previous slide">‹</button><span></span>' +
      '<button type="button" aria-label="Next slide">›</button>';
    document.body.appendChild(nav);
    var count = nav.querySelector("span");

    function go(n, quiet) {
      at = Math.max(0, Math.min(slides.length - 1, n));
      Array.prototype.forEach.call(slides, function (s, i) { s.classList.toggle("on", i === at); });
      count.textContent = (at + 1) + " / " + slides.length;
      // A sealed frame may not change its own address; the deck works without it.
      if (!quiet) try { history.replaceState(null, "", "#" + (at + 1)); } catch (e) { }
    }
    function fromHash() { return (parseInt(location.hash.slice(1), 10) || 1) - 1; }

    nav.firstChild.addEventListener("click", function () { go(at - 1); });
    nav.lastChild.addEventListener("click", function () { go(at + 1); });
    document.addEventListener("keydown", function (e) {
      if (e.altKey || e.ctrlKey || e.metaKey) return;
      var k = e.key;
      if (k === "ArrowRight" || k === "ArrowDown" || k === "PageDown" || k === " ") go(at + 1);
      else if (k === "ArrowLeft" || k === "ArrowUp" || k === "PageUp") go(at - 1);
      else if (k === "Home") go(0);
      else if (k === "End") go(slides.length - 1);
      else return;
      e.preventDefault();
    });
    window.addEventListener("hashchange", function () { go(fromHash(), true); });

    deck.classList.add("js");
    deck.setAttribute("data-slides", slides.length);
    go(fromHash(), true);
  }

  // ---- Links: inside Clayo the viewer opens them, after asking ----

  function setupLinks() {
    if (!embedded) return;
    document.addEventListener("click", function (e) {
      var a = e.target.closest ? e.target.closest("a[href]") : null;
      if (!a) return;
      e.preventDefault();
      var href = a.getAttribute("href");
      if (href.charAt(0) === "#") {
        var to = document.getElementById(href.slice(1));
        if (to) to.scrollIntoView();
      } else {
        window.parent.postMessage({ t: "link", href: a.href }, "*");
      }
    });
  }

  // ---- Start ----

  setupTables();
  setupDeck();
  setupLinks();

  function retheme() { if (window.Chart) drawCharts(); if (window.mermaid) drawDiagrams(); }
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", retheme);
  new MutationObserver(retheme).observe(root, { attributes: true, attributeFilter: ["data-theme"] });
  window.addEventListener("beforeprint", function () { charts.forEach(function (c) { if (c.chart) c.chart.resize(); }); });

  function settle(p) { return p.catch(function (e) { console.error("clayo-page: " + e.message); }); }
  Promise.all([settle(setupCharts()), settle(setupDiagrams())]).then(function () {
    root.setAttribute("data-ready", "1");
    document.dispatchEvent(new Event("clayo-page:ready"));
    if (embedded) window.parent.postMessage({ t: "ready" }, "*");
  });
})();
