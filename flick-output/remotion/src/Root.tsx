import type {FC} from 'react';
import {Composition} from 'remotion';
import {Scene1Hook} from './scenes/Scene1Hook';
import {Scene2PixelsToTree} from './scenes/Scene2PixelsToTree';
import {Scene3Receipts} from './scenes/Scene3Receipts';
import {Scene4Close} from './scenes/Scene4Close';
import {REEL_TOTAL, S1, S2, S3, S4, TelekinesisReel} from './TelekinesisReel';

const FPS = 30;
const W = 1080;
const H = 1920; // 9:16 (Shorts / Stories)

export const RemotionRoot: FC = () => {
  return (
    <>
      <Composition id="TelekinesisReel" component={TelekinesisReel} durationInFrames={REEL_TOTAL} fps={FPS} width={W} height={H} />
      {/* Individual scenes for review */}
      <Composition id="Scene1Hook" component={Scene1Hook} durationInFrames={S1} fps={FPS} width={W} height={H} />
      <Composition id="Scene2PixelsToTree" component={Scene2PixelsToTree} durationInFrames={S2} fps={FPS} width={W} height={H} />
      <Composition id="Scene3Receipts" component={Scene3Receipts} durationInFrames={S3} fps={FPS} width={W} height={H} />
      <Composition id="Scene4Close" component={Scene4Close} durationInFrames={S4} fps={FPS} width={W} height={H} />
    </>
  );
};
