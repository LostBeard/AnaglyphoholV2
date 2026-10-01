// Page-world video cost sampler. Run once to START: turns Stats on (if off), plays every <video>, and records each
// change of the video's anaglyphohol-cost attribute (written by the content script per rendered frame in Stats mode)
// into window.__costSamples with a timestamp. Run read-cost.js later to read the summary - nothing polls in between,
// so the measurement window carries no CDP traffic.
(() => {
  const host = [...document.querySelectorAll('*')].find(e => e.shadowRoot && e.shadowRoot.querySelector('.extension-content'));
  if (!host) return 'no overlay';
  const stats = [...host.shadowRoot.querySelectorAll('button')].find(b => b.title === 'Toggle Stats');
  const video = document.querySelector('video');
  if (!video) return 'no video';
  window.__costSamples = [];
  window.__costObserver?.disconnect();
  window.__costObserver = new MutationObserver(() => {
    window.__costSamples.push({ t: performance.now(), v: video.getAttribute('anaglyphohol-cost') });
  });
  window.__costObserver.observe(video, { attributes: true, attributeFilter: ['anaglyphohol-cost'] });
  const statsWasOn = video.hasAttribute('anaglyphohol-cost');
  if (stats && !statsWasOn) stats.click();
  video.muted = true;
  video.play();
  return `sampling; stats ${statsWasOn ? 'already on' : 'clicked on'}; video ${video.videoWidth}x${video.videoHeight}`;
})()
