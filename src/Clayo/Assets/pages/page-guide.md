# Making a Clayo page

A page is one HTML file that Clayo shows beside the terminal: a report, a dashboard, a
diagram or a slide deck. The design is already done. You write content and data.

## 1. The contract

- One file: `.clayo/pages/<name>.html`. Name in kebab-case from the title
  (`test-results.html`); if it exists and is a different page, add `-2`.
- Start from a template (section 2). Keep these head lines exactly:

```html
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="clayo-page" content="1">
<title>Test results</title>
<link rel="stylesheet" href="clayo-page.css">
```

  and `<script src="clayo-page.js"></script>` as the last line of `<body>`.
- `<title>`: 2 to 4 words, a name, not a sentence.
- Everything else is inside the file. Images as `data:` URIs or inline SVG, data as inline
  JSON. Never link a file from the project; the page can't read it.
- The page has no network. Don't `fetch`, don't use forms, fonts or analytics. If you
  really need a library, load an exact version from `cdnjs.cloudflare.com`,
  `cdn.jsdelivr.net` or `unpkg.com`. Charts and diagrams need none (section 4).
- Never put secrets, tokens, passwords or `.env` values in a page. Pages get shared.

## 2. Pick a template

Copy one from `.clayo/templates/` to `.clayo/pages/<name>.html`, then fill it in.

| Template | Use it for |
|---|---|
| `report.html` | Findings, reviews, summaries, explanations, comparisons |
| `dashboard.html` | Numbers and status: metrics, test runs, usage, progress |
| `slides.html` | Anything to present. One idea per slide |

An answer that fits in a few lines stays in chat. Don't make a page for it.

## 3. Components

A page is `<main class="page">` holding a `<header>` and `<section>`s. Use these classes;
they work on a phone and in light and dark.

```html
<header><h1>Title</h1><p class="lead">The conclusion in one or two sentences.</p></header>

<section class="kpis">                              <!-- headline numbers -->
  <div class="kpi good"><b>98%</b><span>label</span><small>+2 vs last run</small></div>
</section>                                          <!-- kpi: plain, good, warn or bad -->

<div class="card"><h3>Title</h3><p>Text</p></div>   <!-- a boxed block -->
<div class="grid">…</div>                           <!-- children side by side, stacked on a phone -->
<div class="row">…</div>                            <!-- small things in a line: badges, links -->
<div class="stack">…</div>                          <!-- children in a column with even gaps -->

<span class="badge good">passed</span>              <!-- badge: plain, good, warn, bad, info -->
<div class="callout warn"><strong>Note.</strong> Text.</div>   <!-- callout: info (default), warn, bad, good -->

<table class="table" data-sortable>                 <!-- data-sortable: click a header to sort -->
  <thead><tr><th>Name</th><th>Count</th></tr></thead>
  <tbody><tr><td>auth</td><td>4</td></tr></tbody>
</table>                                            <!-- number columns align right by themselves -->

<ul class="timeline">                               <!-- events in order -->
  <li class="good"><time>10:42</time><strong>Deployed</strong> v1.4 to production</li>
</ul>

<div class="compare">                               <!-- options side by side; pick = the recommended one -->
  <div class="card pick"><h3>Option A</h3><span class="badge good">Recommended</span><p>Why.</p></div>
  <div class="card"><h3>Option B</h3><p>Why not.</p></div>
</div>

<ol class="steps"><li><strong>First.</strong> Detail.</li></ol>   <!-- numbered steps -->

<figure><img alt="What it shows" src="data:image/png;base64,…"><figcaption>Caption</figcaption></figure>
<pre><code>code or terminal output</code></pre>
<progress value="70" max="100"></progress>
<small>fine print</small>  <span class="muted">secondary text</span>
```

Slides: `<main class="deck">` with one `<section class="slide">` each, 16:9. Layouts:
`slide title` (opening), `slide` (heading and bullets), `slide split` (two columns: put
exactly two children in it), `slide big-number` (`<b>42%</b>` and a `.lead`), `slide quote`
(`<blockquote>` and `<cite>`). Everything above works inside a slide. A slide doesn't
scroll: at most 4 bullets, or one chart, or one table of 5 rows.

## 4. Charts and diagrams

A chart is data, not markup. Put this where the chart goes:

```html
<script type="application/json" class="chart">
{"type":"bar","title":"Failures by module","labels":["auth","api","ui"],
 "series":[{"name":"failed","data":[4,0,1]}]}
</script>
```

| `type` | Use it for | Notes |
|---|---|---|
| `bar` | Comparing categories | Several series group; `"stacked":true` stacks; `"horizontal":true` for long labels |
| `line` | A trend over time | 4 or more points, at most 6 series |
| `area` | A trend where the volume matters | Same data as `line` |
| `pie`, `doughnut` | Parts of a whole | One series, at most 5 slices; otherwise use `bar` |
| `scatter` | Two measures against each other | `"data":[[x,y],[x,y]]`, no `labels` |

Optional: `"xLabel"`, `"yLabel"` (name the unit: `"yLabel":"ms"`). Colours, legend and
theme are automatic; don't set them. The JSON must be valid: double quotes, no comments,
no trailing commas.

A diagram is Mermaid text. Use it for flows, sequences, dependencies and states:

```html
<pre class="mermaid">
flowchart LR
  A[Request] --> B{Cached?}
  B -- yes --> C[Return]
  B -- no --> D[Fetch] --> C
</pre>
```

`flowchart`, `sequenceDiagram`, `classDiagram`, `stateDiagram-v2`, `erDiagram`, `gantt` all
work. Keep node labels short; put text with brackets or punctuation in quotes
(`A["parse(x)"]`); write `&lt;` for `<`. Prefer `LR` for up to 6 steps, `TD` for more. Don't
set colours or a theme.

## 5. Writing

- Lead with the answer. The `<h1>` names the thing, the `.lead` says what you found.
- Numbers go in KPIs, tables or charts, not in paragraphs.
- Short sections, each with an `<h2>` that says the point, not the topic
  ("Auth tests fail on token refresh", not "Results").
- One chart, one message. If a chart needs a paragraph to explain it, use a table.
- Say what a colour means in words too: a red badge still reads "failed".
- No emoji as icons. No decoration that carries no information.

## 6. Speed

A page should take about a minute. What makes it slow is writing more than you need.

- Copy the template and fill it in. Don't restyle it and don't rewrite its structure.
- No `<style>` block unless something truly can't be done with the classes; then at most
  20 lines, using the tokens (`var(--surface)`, `var(--border)`, `var(--muted)`,
  `var(--accent)`, `var(--good)`, `var(--warn)`, `var(--bad)`, `var(--s1)` to `var(--s7)`).
  Never hard-code a colour: it breaks the other theme.
- No inline `style=""` for layout, no custom JavaScript for anything in section 3 or 4.
- Write the file in one go. Don't read `clayo-page.css` or `clayo-page.js`.
- Asked to change a page you made: edit that file. Don't make a new one.

## 7. Check

When you've written the file, reply with one line saying what the page shows. Clayo opens it
by itself.
