// protocol.js — C# RegisterMap 的 JS 镜像（导出: unpackAscii, hex4, agtPackAscii）

// 与 RegisterMap.UnpackAscii 一致的客户端解包（high/low 字节序，去尾部 \0 与空白）
function unpackAscii(words, byteOrder) {
  const highFirst = byteOrder !== 'low';
  let s = '';
  for (const w of words) {
    const lo = highFirst ? (w & 0xFF) : (w >> 8);
    const hi = highFirst ? (w >> 8) : (w & 0xFF);
    for (const b of [lo, hi]) s += b >= 32 && b < 127 ? String.fromCharCode(b) : '';
  }
  return s.trimEnd();
}
const hex4 = w => w.toString(16).padStart(4, '0');

// ASCII 打包（与 RegisterMap.PackAscii 一致：32 字符 ↔ 16 字，按字节序）——PLC 单机测试页③ 复用
function agtPackAscii(s, bo) {
  const b = (s || '').padEnd(32, ' ').slice(0, 32);
  const words = [];
  for (let i = 0; i < 16; i++) {
    const lo = b.charCodeAt(2 * i), hi = b.charCodeAt(2 * i + 1);
    words.push(bo === 'low' ? ((lo << 8) | hi) : ((hi << 8) | lo));
  }
  return words;
}
