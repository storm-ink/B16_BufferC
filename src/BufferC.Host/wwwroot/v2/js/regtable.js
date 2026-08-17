// regtable.js — 共享寄存器表渲染器（导出: buildRegTable）
// 统一原三处近重复的表构建（buildDetail / renderPlctEcho / mcsBuild），保留 data-* 属性契约，
// 使原地更新器（renderDetail/mcsRefresh 的 data-r/data-m/data-mid/data-sid/data-hid）不改仍可用
//
// spec: { cols: [表头...], rows: [{ cls: '行类名(可选)', cells: [单元格...] }] }
// 单元格类型:
//   { t:'text', v:html }               普通文本/HTML 单元格
//   { t:'r', k:key }                   data-r 只读值单元格（原地更新目标）
//   { t:'sid', k:st } / { t:'hid', k:st }  ID 区快照/HEX 单元格
//   { t:'m', k:addr }                  data-m 可编辑输入（mcs-write-cell）
//   { t:'mid', k:st }                  data-mid 可编辑输入（mcs-write-id）
//   { t:'scan' }                       #mcsScanCode 可编辑输入（mcs-write-scan）
//   { t:'id', k:id, v:html }           指定 id 单元格
//   { t:'raw', attrs, v:html }         带任意属性的单元格
function buildRegTable(el, spec) {
  el.innerHTML = '<tr>' + spec.cols.map(c => `<th>${c}</th>`).join('') + '</tr>' +
    spec.rows.map(r => '<tr' + (r.cls ? ` class="${r.cls}"` : '') + '>' +
      r.cells.map(c => {
        if (c.t === 'text') return `<td>${c.v}</td>`;
        if (c.t === 'r') return `<td data-r="${c.k}"></td>`;
        if (c.t === 'sid') return `<td data-sid="${c.k}"></td>`;
        if (c.t === 'hid') return `<td class="mono" data-hid="${c.k}">—</td>`;
        if (c.t === 'm') return `<td><input data-m="${c.k}" size="6" data-enter="blur" data-action="mcs-write-cell"></td>`;
        if (c.t === 'mid') return `<td><input data-mid="${c.k}" size="36" data-enter="blur" data-action="mcs-write-id"></td>`;
        if (c.t === 'scan') return `<td><input id="mcsScanCode" size="36" data-enter="blur" data-action="mcs-write-scan"></td>`;
        if (c.t === 'id') return `<td id="${c.k}">${c.v || ''}</td>`;
        if (c.t === 'raw') return `<td ${c.attrs || ''}>${c.v || ''}</td>`;
        return '<td></td>';
      }).join('') + '</tr>').join('');
}
