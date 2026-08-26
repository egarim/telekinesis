import {AbsoluteFill, Img, interpolate, spring, staticFile, useCurrentFrame, useVideoConfig} from 'remotion';
import {C, FONT_BODY, FONT_DISPLAY} from '../theme';

// "Telekinesis. Move things without touching them."
export const Scene4Close: React.FC = () => {
  const frame = useCurrentFrame();
  const {fps, width} = useVideoConfig();

  const rise = spring({frame, fps, config: {damping: 140}});
  const mascotY = interpolate(rise, [0, 1], [80, 0]);
  // the nudged dot drifts aside as the mascot settles
  const nudge = interpolate(frame, [24, 54], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const word = interpolate(frame, [40, 60], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const underline = interpolate(frame, [58, 82], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  const tag = interpolate(frame, [76, 94], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});

  const tile = width * 0.42;

  return (
    <AbsoluteFill style={{background: C.charcoal, alignItems: 'center', justifyContent: 'center'}}>
      <div style={{position: 'relative', transform: `translateY(${mascotY}px)`, opacity: rise, marginTop: -160}}>
        <Img
          src={staticFile('brand-assets/mascot.png')}
          style={{width: tile, height: tile, borderRadius: 64, boxShadow: '0 30px 90px rgba(0,0,0,0.5)'}}
        />
        {/* the little dot it nudges aside */}
        <div
          style={{
            position: 'absolute',
            top: tile * 0.32,
            right: -40 - nudge * 70,
            width: 46,
            height: 46,
            borderRadius: '50%',
            background: C.morado,
            opacity: 0.9,
            boxShadow: `0 0 24px ${C.morado}`,
          }}
        />
      </div>

      <div style={{position: 'absolute', top: '58%', textAlign: 'center', width: '92%'}}>
        <div style={{fontFamily: FONT_DISPLAY, color: C.cream, fontSize: 118, fontWeight: 600, opacity: word, letterSpacing: -1}}>
          Telekinesis
        </div>
        <div
          style={{
            height: 8,
            width: `${underline * 46}%`,
            background: C.morado,
            borderRadius: 8,
            margin: '18px auto 0',
            boxShadow: `0 0 18px ${C.morado}`,
          }}
        />
        <div style={{fontFamily: FONT_BODY, color: C.muted, fontSize: 44, marginTop: 30, opacity: tag}}>
          move things without touching them
        </div>
      </div>
    </AbsoluteFill>
  );
};
