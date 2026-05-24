using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RevitMCP.Addin.Export;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Reports;

/// <summary>
/// Generates a standalone offline HTML dashboard from an <see cref="IssueReportDto"/>.
/// No external CDN dependencies — all CSS and JS is embedded.
/// </summary>
public class IssueHtmlDashboardExporter
{
    private static readonly ExportPathService _pathService = new();

    private static readonly JsonSerializerSettings _jsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    /// <summary>
    /// Exports the report as a standalone HTML file.
    /// </summary>
    /// <param name="report">The issue report to render.</param>
    /// <param name="outputDir">Override output directory. Uses default export path if null.</param>
    /// <param name="fileName">Override file name (without path). Auto-generated if null.</param>
    /// <param name="includeEmbeddedJson">If true, embeds the raw report JSON in the page for debugging.</param>
    /// <returns>Absolute path to the saved HTML file.</returns>
    public string Export(IssueReportDto report, string? outputDir = null, string? fileName = null, bool includeEmbeddedJson = true)
    {
        var dir = string.IsNullOrWhiteSpace(outputDir)
            ? _pathService.ExportDirectory
            : outputDir;

        Directory.CreateDirectory(dir);

        var safeName = string.IsNullOrWhiteSpace(fileName)
            ? $"QA_Dashboard_{SanitizeName(report.Title)}_{report.CreatedAt:yyyy-MM-dd_HHmm}.html"
            : fileName;

        var filePath = Path.Combine(dir, safeName);
        var html = BuildHtml(report, includeEmbeddedJson);
        File.WriteAllText(filePath, html, Encoding.UTF8);
        return filePath;
    }

    private static string BuildHtml(IssueReportDto report, bool includeEmbeddedJson)
    {
        var reportJson = JsonConvert.SerializeObject(report, _jsonSettings);

        var sb = new StringBuilder();
        sb.Append(HtmlTemplate
            .Replace("__REPORT_JSON__", reportJson)
            .Replace("__INCLUDE_JSON_SECTION__", includeEmbeddedJson ? BuildRawJsonSection(reportJson) : string.Empty));
        return sb.ToString();
    }

    private static string BuildRawJsonSection(string json) =>
        $"<details style=\"margin-top:1.5rem\"><summary style=\"cursor:pointer;font-size:.8rem;color:#6b7280\">Raw JSON</summary>" +
        $"<pre style=\"font-size:.75rem;overflow:auto;background:#f1f5f9;padding:1rem;border-radius:6px;max-height:400px\">{EscapeHtml(json)}</pre></details>";

    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c))
                     .Trim('_');
    }

    // -------------------------------------------------------------------------
    // HTML template — single self-contained file, no external dependencies
    // Placeholders: __REPORT_JSON__  __INCLUDE_JSON_SECTION__
    // -------------------------------------------------------------------------
    private const string HtmlTemplate = @"<!DOCTYPE html>
<html lang=""et"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>QA Dashboard</title>
<style>
*,*::before,*::after{box-sizing:border-box}
:root{
  --bg:#f8f9fa;--surface:#fff;--primary:#2563eb;
  --critical:#dc2626;--error:#ea580c;--warning:#d97706;--info:#0284c7;
  --open:#6b7280;--resolved:#16a34a;
  --text:#111827;--muted:#6b7280;--border:#e5e7eb;
  --radius:8px;--shadow:0 1px 3px rgba(0,0,0,.12)
}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',system-ui,sans-serif;
     background:var(--bg);color:var(--text);margin:0;padding:0}
.container{max-width:1400px;margin:0 auto;padding:1rem 1.5rem}
.header{background:var(--primary);color:#fff;padding:1.25rem 1.5rem;margin-bottom:1.5rem}
.header h1{margin:0 0 .25rem;font-size:1.4rem;font-weight:700}
.header .meta{font-size:.85rem;opacity:.85}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:1rem;margin-bottom:1.5rem}
.card{background:var(--surface);border-radius:var(--radius);box-shadow:var(--shadow);
      padding:1rem;text-align:center;border-top:4px solid transparent}
.card.total{border-color:var(--primary)}.card.critical{border-color:var(--critical)}
.card.error{border-color:var(--error)}.card.warning{border-color:var(--warning)}.card.info{border-color:var(--info)}
.card-count{font-size:2.2rem;font-weight:800;line-height:1}
.card-label{font-size:.8rem;color:var(--muted);margin-top:.4rem;text-transform:uppercase;letter-spacing:.05em}
.filters{background:var(--surface);border-radius:var(--radius);box-shadow:var(--shadow);
         padding:1rem;margin-bottom:1rem;display:flex;flex-wrap:wrap;gap:.75rem;align-items:flex-end}
.filters label{font-size:.8rem;font-weight:600;color:var(--muted);display:block;margin-bottom:.2rem}
.filters select,.filters input[type=search]{
  border:1px solid var(--border);border-radius:4px;padding:.35rem .6rem;
  font-size:.875rem;background:#fff;min-width:130px}
.filters input[type=search]{min-width:200px}
.btn-clear{padding:.35rem .85rem;border:1px solid var(--border);border-radius:4px;
           background:#fff;cursor:pointer;font-size:.875rem}
.btn-clear:hover{background:var(--bg)}
.table-wrap{background:var(--surface);border-radius:var(--radius);box-shadow:var(--shadow);overflow:auto}
table{width:100%;border-collapse:collapse;font-size:.875rem}
th,td{padding:.6rem .75rem;text-align:left;border-bottom:1px solid var(--border)}
th{background:#f1f5f9;font-weight:600;font-size:.8rem;text-transform:uppercase;
   letter-spacing:.04em;cursor:pointer;user-select:none;white-space:nowrap}
th:hover{background:#e2e8f0}th .si{margin-left:.25rem;opacity:.6}
tr:hover>td{background:#f8faff}
tr.dr>td{background:#f8faff;padding:.4rem 1rem;font-size:.8rem;color:var(--muted)}
.badge{display:inline-block;padding:.15rem .5rem;border-radius:999px;font-size:.74rem;font-weight:700;text-transform:uppercase;letter-spacing:.04em}
.badge-critical{background:#fee2e2;color:var(--critical)}.badge-error{background:#ffedd5;color:var(--error)}
.badge-warning{background:#fef3c7;color:var(--warning)}.badge-info{background:#e0f2fe;color:var(--info)}
.badge-open{background:#f3f4f6;color:var(--open)}.badge-resolved{background:#dcfce7;color:var(--resolved)}
.badge-acknowledged{background:#fef9c3;color:#92400e}
.no-data{text-align:center;padding:3rem;color:var(--muted)}
.ci{font-size:.8rem;color:var(--muted);padding:.5rem .75rem;border-bottom:1px solid var(--border)}
.xbtn{background:none;border:none;cursor:pointer;font-size:.9rem;color:var(--muted);padding:0 .25rem;line-height:1}
.xbtn:hover{color:var(--text)}
.footer{text-align:center;padding:1.5rem;font-size:.8rem;color:var(--muted)}
</style>
</head>
<body>
<div class=""header"">
  <div style=""max-width:1400px;margin:0 auto;padding:0 1.5rem"">
    <h1 id=""rpt-title""></h1>
    <div class=""meta"" id=""rpt-meta""></div>
  </div>
</div>
<div class=""container"">
  <div class=""cards"">
    <div class=""card total""><div class=""card-count"" id=""cnt-total"">0</div><div class=""card-label"">Kokku</div></div>
    <div class=""card critical""><div class=""card-count"" id=""cnt-critical"">0</div><div class=""card-label"">Kriitiline</div></div>
    <div class=""card error""><div class=""card-count"" id=""cnt-error"">0</div><div class=""card-label"">Viga</div></div>
    <div class=""card warning""><div class=""card-count"" id=""cnt-warning"">0</div><div class=""card-label"">Hoiatus</div></div>
    <div class=""card info""><div class=""card-count"" id=""cnt-info"">0</div><div class=""card-label"">Info</div></div>
  </div>
  <div class=""filters"">
    <div><label>Otsing</label><input id=""f-q"" type=""search"" placeholder=""Otsi...""></div>
    <div><label>Tõsidus</label><select id=""f-sev""><option value="""">Kõik</option></select></div>
    <div><label>Staatus</label><select id=""f-sta""><option value="""">Kõik</option></select></div>
    <div><label>Kategooria</label><select id=""f-cat""><option value="""">Kõik</option></select></div>
    <div><label>Distsipliin</label><select id=""f-dis""><option value="""">Kõik</option></select></div>
    <div><label>Tööriiist</label><select id=""f-tool""><option value="""">Kõik</option></select></div>
    <button class=""btn-clear"" id=""btn-clear"">Tühjenda filtrid</button>
  </div>
  <div class=""table-wrap"">
    <div class=""ci"" id=""ci"">Laen...</div>
    <table>
      <thead>
        <tr>
          <th style=""width:32px""></th>
          <th data-col=""issueId"">ID</th>
          <th data-col=""severity"">Tõsidus</th>
          <th data-col=""status"">Staatus</th>
          <th data-col=""category"">Kategooria</th>
          <th data-col=""title"">Pealkiri</th>
          <th data-col=""discipline"">Distsipliin</th>
          <th data-col=""sheetNumber"">Leht</th>
          <th data-col=""elementId"">Element ID</th>
          <th data-col=""sourceTool"">Tööriist</th>
        </tr>
      </thead>
      <tbody id=""tbody""></tbody>
    </table>
    <div class=""no-data"" id=""no-data"" style=""display:none"">Filtrile vastavaid probleeme ei leitud.</div>
  </div>
  <div class=""footer"" id=""footer""></div>
  __INCLUDE_JSON_SECTION__
</div>
<script>
const R=__REPORT_JSON__;
const issues=R.Issues||[];
let sCol='severity',sDir=1;
const sevOrd={Critical:0,Error:1,Warning:2,Info:3};
const expanded=new Set();

function init(){
  document.getElementById('rpt-title').textContent=R.Title||'QA Aruanne';
  const mp=R.ModelTitle?' | '+R.ModelTitle:'';
  document.getElementById('rpt-meta').textContent='Run ID: '+R.RunId+' | '+fmtDate(R.CreatedAt)+mp;
  document.getElementById('cnt-total').textContent=issues.length;
  document.getElementById('cnt-critical').textContent=issues.filter(i=>i.Severity==='Critical').length;
  document.getElementById('cnt-error').textContent=issues.filter(i=>i.Severity==='Error').length;
  document.getElementById('cnt-warning').textContent=issues.filter(i=>i.Severity==='Warning').length;
  document.getElementById('cnt-info').textContent=issues.filter(i=>i.Severity==='Info').length;
  pop('f-sev',[...new Set(issues.map(i=>i.Severity).filter(Boolean))].sort((a,b)=>(sevOrd[a]??9)-(sevOrd[b]??9)));
  pop('f-sta',[...new Set(issues.map(i=>i.Status).filter(Boolean))].sort());
  pop('f-cat',[...new Set(issues.map(i=>i.Category).filter(Boolean))].sort());
  pop('f-dis',[...new Set(issues.map(i=>i.Discipline).filter(Boolean))].sort());
  pop('f-tool',[...new Set(issues.map(i=>i.SourceTool).filter(Boolean))].sort());
  ['f-q','f-sev','f-sta','f-cat','f-dis','f-tool'].forEach(id=>
    document.getElementById(id).addEventListener('input',render));
  document.getElementById('btn-clear').addEventListener('click',clearF);
  document.querySelectorAll('th[data-col]').forEach(th=>{
    const col=th.dataset.col;
    th.innerHTML+=`<span class=""si"" id=""si-${col}""></span>`;
    th.addEventListener('click',()=>sortBy(col));
  });
  document.getElementById('footer').textContent=
    'Genereeritud: '+fmtDate(R.CreatedAt)+' | Tööriistad: '+(R.SourceTools||[]).join(', ');
  updateSI();render();
}

function pop(id,vals){
  const sel=document.getElementById(id);
  vals.forEach(v=>{const o=document.createElement('option');o.value=o.textContent=v;sel.appendChild(o);});
}

function getF(){
  const q=document.getElementById('f-q').value.toLowerCase();
  const sev=document.getElementById('f-sev').value;
  const sta=document.getElementById('f-sta').value;
  const cat=document.getElementById('f-cat').value;
  const dis=document.getElementById('f-dis').value;
  const tool=document.getElementById('f-tool').value;
  return issues.filter(i=>{
    if(sev&&i.Severity!==sev)return false;
    if(sta&&i.Status!==sta)return false;
    if(cat&&i.Category!==cat)return false;
    if(dis&&i.Discipline!==dis)return false;
    if(tool&&i.SourceTool!==tool)return false;
    if(q){
      const h=[i.IssueId,i.Title,i.Description,i.Category,i.SheetNumber,i.SuggestedFix,i.SourceTool,i.ElementId].join(' ').toLowerCase();
      if(!h.includes(q))return false;
    }
    return true;
  });
}

function sorted(arr){
  return [...arr].sort((a,b)=>{
    let av=a[sCol],bv=b[sCol];
    if(sCol==='severity'){av=sevOrd[av]??9;bv=sevOrd[bv]??9;}
    if(av==null)return 1;if(bv==null)return-1;
    return av<bv?-sDir:av>bv?sDir:0;
  });
}

function render(){
  const data=sorted(getF());
  const tbody=document.getElementById('tbody');
  tbody.innerHTML='';
  document.getElementById('ci').textContent=data.length+' probleemi '+issues.length+' hulgast';
  document.getElementById('no-data').style.display=data.length?'none':'block';
  data.forEach(iss=>{
    const isX=expanded.has(iss.IssueId);
    const tr=document.createElement('tr');
    tr.innerHTML=
      `<td><button class=""xbtn"" data-id=""${e(iss.IssueId)}"">${isX?'&#9660;':'&#9654;'}</button></td>`+
      `<td style=""font-family:monospace;font-size:.78rem"">${e(iss.IssueId)}</td>`+
      `<td>${badge(iss.Severity)}</td>`+
      `<td>${sbadge(iss.Status)}</td>`+
      `<td>${e(iss.Category)}</td>`+
      `<td>${e(iss.Title)}</td>`+
      `<td>${e(iss.Discipline||'')}</td>`+
      `<td>${e(iss.SheetNumber||'')}</td>`+
      `<td style=""font-family:monospace;font-size:.78rem"">${iss.ElementId||''}</td>`+
      `<td style=""font-size:.78rem"">${e(iss.SourceTool||'')}</td>`;
    tr.querySelector('.xbtn').addEventListener('click',ev=>{
      const id=ev.currentTarget.dataset.id;
      expanded.has(id)?expanded.delete(id):expanded.add(id);render();
    });
    tbody.appendChild(tr);
    if(isX){
      const dr=document.createElement('tr');
      dr.className='dr';
      let det=`<b>Kirjeldus:</b> ${e(iss.Description||'')}`;
      if(iss.SuggestedFix)det+=`<br><b>Soovitatav lahendus:</b> ${e(iss.SuggestedFix)}`;
      if(iss.FilePath)det+=`<br><b>Fail:</b> ${e(iss.FilePath)}`;
      if(iss.Phase)det+=`<br><b>Etapp:</b> ${e(iss.Phase)}`;
      if(iss.LinkedElementId)det+=`<br><b>Seotud element:</b> ${iss.LinkedElementId}`;
      dr.innerHTML=`<td colspan=""10"">${det}</td>`;
      tbody.appendChild(dr);
    }
  });
}

function sortBy(col){sDir=sCol===col?-sDir:1;sCol=col;updateSI();render();}
function updateSI(){
  document.querySelectorAll('.si').forEach(el=>el.textContent='');
  const el=document.getElementById('si-'+sCol);
  if(el)el.textContent=sDir===1?' ▲':' ▼';
}
function clearF(){
  ['f-q','f-sev','f-sta','f-cat','f-dis','f-tool'].forEach(id=>{document.getElementById(id).value='';});
  render();
}
function badge(s){
  const m={Critical:'critical',Error:'error',Warning:'warning',Info:'info'};
  const l={Critical:'Kriitiline',Error:'Viga',Warning:'Hoiatus',Info:'Info'};
  return`<span class=""badge badge-${m[s]||'info'}"">${l[s]||s||''}</span>`;
}
function sbadge(s){
  if(!s)return'';
  const m={Open:'open',Resolved:'resolved',Acknowledged:'acknowledged'};
  const l={Open:'Avatud',Resolved:'Lahendatud',Acknowledged:'Märgitud'};
  return`<span class=""badge badge-${m[s]||'open'}"">${l[s]||s}</span>`;
}
function e(s){if(!s)return'';return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/[""]/g,'&quot;');}
function fmtDate(d){try{return new Date(d).toLocaleString('et-EE');}catch{return String(d);}}
init();
</script>
</body>
</html>";
}
