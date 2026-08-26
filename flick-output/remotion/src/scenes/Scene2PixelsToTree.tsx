import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {C, FONT_BODY, FONT_DISPLAY} from '../theme';

const NODES = ['Window', 'ToolBar', 'Button · "Open Parent"'];

export const Scene2PixelsToTree: React.FC = () => {
  const frame = useCurrentFrame();
  const {fps, width, height} = useVideoConfig();

  // Pixel grid dissolves out over the first ~1.2s.
  const pix = interpolate(frame, [0, 36], [1, 0], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const cols = 9;
  const rows = 16;
  const cell = width / cols;

  const title = interpolate(frame, [6, 24], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});

  return (
    <AbsoluteFill style={{background: C.charcoal, alignItems: 'center', justifyContent: 'center'}}>
      {/* dissolving pixels */}
      {pix > 0.01 &&
        Array.from({length: cols * rows}).map((_, i) => {
          const cx = i % cols;
          const cy = Math.floor(i / cols);
          const seed = (cx * 7 + cy * 13) % 11;
          const local = interpolate(frame, [seed, seed + 14], [1, 0], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
          return (
            <div
              key={i}
              style={{
                position: 'absolute',
                left: cx * cell,
                top: cy * cell,
                width: cell - 4,
                height: cell - 4,
                borderRadius: 8,
                background: (cx + cy) % 3 === 0 ? C.morado : C.ink,
                opacity: local * 0.55,
              }}
            />
          );
        })}

      <div style={{position: 'absolute', top: '10%', textAlign: 'center', width: '88%'}}>
        <div style={{fontFamily: FONT_BODY, color: C.muted, fontSize: 38, letterSpacing: 1, opacity: title}}>
          not pixels —
        </div>
        <div style={{fontFamily: FONT_DISPLAY, color: C.cream, fontSize: 76, fontWeight: 600, marginTop: 6, opacity: title}}>
          the accessibility tree
        </div>
      </div>

      {/* the tree nodes light up one by one */}
      <div style={{position: 'absolute', top: '34%', width: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 26}}>
        {NODES.map((n, idx) => {
          const start = 40 + idx * 26;
          const s = spring({frame: frame - start, fps, config: {damping: 180}});
          const isLeaf = idx === NODES.length - 1;
          return (
            <div key={n} style={{display: 'flex', flexDirection: 'column', alignItems: 'center', width: '100%'}}>
              <div
                style={{
                  transform: `scale(${0.6 + s * 0.4})`,
                  opacity: s,
                  padding: '26px 40px',
                  borderRadius: 22,
                  marginLeft: idx * 90,
                  background: isLeaf ? C.morado : C.charcoal2,
                  border: `3px solid ${isLeaf ? C.moradoLite : C.ink}`,
                  color: isLeaf ? C.cream : C.muted,
                  fontFamily: FONT_BODY,
                  fontSize: isLeaf ? 46 : 40,
                  fontWeight: isLeaf ? 700 : 500,
                  boxShadow: isLeaf ? `0 0 ${30 * s}px ${C.morado}` : 'none',
                }}
              >
                {n}
              </div>
              {idx < NODES.length - 1 && (
                <div style={{width: 4, height: 34 * s, background: C.moradoLite, opacity: s, marginLeft: idx * 90 + 40}} />
              )}
            </div>
          );
        })}
      </div>
    </AbsoluteFill>
  );
};
