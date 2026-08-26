import {AbsoluteFill, Audio, Sequence, Series, staticFile} from 'remotion';
import {Scene1Hook} from './scenes/Scene1Hook';
import {Scene2PixelsToTree} from './scenes/Scene2PixelsToTree';
import {Scene3Receipts} from './scenes/Scene3Receipts';
import {Scene4Close} from './scenes/Scene4Close';

export const S1 = 96;   // 3.2s
export const S2 = 144;  // 4.8s
export const S3 = 180;  // 6.0s
export const S4 = 120;  // 4.0s
export const REEL_TOTAL = S1 + S2 + S3 + S4; // 540 = 18s

// Action-matched SFX only.
const sfx = (name: string) => staticFile(`sounds/${name}`);

export const TelekinesisReel: React.FC = () => {
  return (
    <AbsoluteFill>
      <Series>
        <Series.Sequence durationInFrames={S1}>
          <Scene1Hook />
        </Series.Sequence>
        <Series.Sequence durationInFrames={S2}>
          <Scene2PixelsToTree />
        </Series.Sequence>
        <Series.Sequence durationInFrames={S3}>
          <Scene3Receipts />
        </Series.Sequence>
        <Series.Sequence durationInFrames={S4}>
          <Scene4Close />
        </Series.Sequence>
      </Series>

      {/* SFX at scene boundaries / action moments */}
      <Sequence from={0} durationInFrames={S1}>
        <Audio src={sfx('riser.mp3')} volume={0.5} />
      </Sequence>
      <Sequence from={S1} durationInFrames={30}>
        <Audio src={sfx('Pop.mp3')} volume={0.6} />
      </Sequence>
      <Sequence from={S1 + S2 + 96} durationInFrames={20}>
        <Audio src={sfx('Click.mp3')} volume={0.7} />
      </Sequence>
      <Sequence from={S1 + S2 + S3} durationInFrames={40}>
        <Audio src={sfx('Impact.mp3')} volume={0.6} />
      </Sequence>
    </AbsoluteFill>
  );
};
