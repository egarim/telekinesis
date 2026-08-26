import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {C, FONT_BODY, FONT_DISPLAY} from '../theme';

// "What if an AI could use your computer without ever looking at the screen?"
export const Scene1Hook: React.FC = () => {
  const frame = useCurrentFrame();
  const {fps, width} = useVideoConfig();

  const screenIn = spring({frame, fps, config: {damping: 200}});
  const blindfold = interpolate(frame, [18, 40], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const line1 = interpolate(frame, [8, 26], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const line2 = interpolate(frame, [30, 48], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const glow = interpolate(frame, [46, 70, 96], [0, 1, 0.7], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});

  const sw = width * 0.62;
  const sh = sw * 1.5;

  return (
    <AbsoluteFill style={{background: C.charcoal, alignItems: 'center', justifyContent: 'center'}}>
      {/* blindfolded screen */}
      <div
        style={{
          width: sw,
          height: sh,
          borderRadius: 44,
          background: C.charcoal2,
          border: `3px solid ${C.ink}`,
          transform: `scale(${0.9 + screenIn * 0.1})`,
          position: 'relative',
          overflow: 'hidden',
          boxShadow: '0 40px 120px rgba(0,0,0,0.5)',
        }}
      >
        {/* dark band = blindfold */}
        <div
          style={{
            position: 'absolute',
            top: '42%',
            left: -20,
            right: -20,
            height: sh * 0.16,
            background: C.charcoal,
            opacity: blindfold,
            transform: `rotate(-4deg)`,
          }}
        />
        {/* the one control that lights up */}
        <div
          style={{
            position: 'absolute',
            bottom: sh * 0.14,
            left: '50%',
            width: 130,
            height: 130,
            marginLeft: -65,
            borderRadius: '50%',
            background: C.morado,
            opacity: glow,
            filter: `blur(2px)`,
            boxShadow: `0 0 ${40 * glow}px ${C.morado}`,
          }}
        />
      </div>

      <div style={{position: 'absolute', top: '13%', textAlign: 'center', width: '86%'}}>
        <div style={{fontFamily: FONT_BODY, color: C.muted, fontSize: 40, letterSpacing: 2, opacity: line1, textTransform: 'uppercase'}}>
          use your computer
        </div>
        <div style={{fontFamily: FONT_DISPLAY, color: C.cream, fontSize: 92, fontWeight: 600, lineHeight: 1.02, marginTop: 10, opacity: line2}}>
          without looking.
        </div>
      </div>
    </AbsoluteFill>
  );
};
