import {AbsoluteFill, interpolate, OffthreadVideo, staticFile, useCurrentFrame} from 'remotion';
import {C, FONT_BODY} from '../theme';

// Real capture: Telekinesis driving Thunar. "find Open Parent -> invoke -> folder changes."
export const Scene3Receipts: React.FC = () => {
  const frame = useCurrentFrame();
  const titleIn = interpolate(frame, [4, 20], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const capIn = interpolate(frame, [30, 46], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  // "NativeAction" tag flashes in around the invoke moment.
  const tag = interpolate(frame, [96, 108, 150, 168], [0, 1, 1, 0.85], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});

  return (
    <AbsoluteFill style={{background: C.charcoal, alignItems: 'center', justifyContent: 'center'}}>
      <div style={{position: 'absolute', top: '6%', textAlign: 'center', width: '90%', opacity: titleIn}}>
        <div style={{fontFamily: FONT_BODY, color: C.cream, fontSize: 52, fontWeight: 700}}>a real app — driven live</div>
        <div style={{fontFamily: FONT_BODY, color: C.muted, fontSize: 34, marginTop: 6}}>no mouse · no pixels</div>
      </div>

      {/* device-framed real footage */}
      <div
        style={{
          width: '82%',
          borderRadius: 40,
          overflow: 'hidden',
          border: `4px solid ${C.ink}`,
          boxShadow: '0 40px 120px rgba(0,0,0,0.55)',
          background: '#000',
        }}
      >
        <OffthreadVideo
          src={staticFile('brand-assets/thunar-9x16.mp4')}
          playbackRate={2.2}
          muted
          style={{width: '100%', display: 'block'}}
        />
      </div>

      {/* NativeAction pill */}
      <div
        style={{
          position: 'absolute',
          top: '20%',
          right: '8%',
          transform: `scale(${0.7 + tag * 0.3})`,
          opacity: tag,
          background: C.morado,
          color: C.cream,
          fontFamily: FONT_BODY,
          fontWeight: 700,
          fontSize: 34,
          padding: '14px 26px',
          borderRadius: 999,
          boxShadow: `0 0 ${26 * tag}px ${C.morado}`,
        }}
      >
        path = NativeAction
      </div>

      <div style={{position: 'absolute', bottom: '7%', textAlign: 'center', width: '88%', opacity: capIn}}>
        <div
          style={{
            fontFamily: FONT_BODY,
            color: C.cream,
            fontSize: 40,
            fontWeight: 600,
            background: C.charcoal2,
            border: `2px solid ${C.ink}`,
            borderRadius: 18,
            padding: '18px 22px',
            display: 'inline-block',
          }}
        >
          find <span style={{color: C.moradoLite}}>"Open Parent"</span> → invoke → the folder changes
        </div>
      </div>
    </AbsoluteFill>
  );
};
