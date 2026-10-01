// Requests ONE profiled video frame from the content script (Stats mode must be on: run sample-cost.js first) and
// collects the result. Set window.__profileMode = "ops" first for per-op timing (slower frame), else "basic".
// First run: sets the request and returns "requested". Run again (after a frame) to read the report; each '|' field
// is printed on its own line.
(() => {
  const video = document.querySelector('video');
  if (!video) return 'no video';
  const result = video.getAttribute('anaglyphohol-profile');
  if (result && window.__profilePending) {
    window.__profilePending = false;
    video.removeAttribute('anaglyphohol-profile');
    return result.split(' | ').join('\n');
  }
  if (window.__profilePending) return 'still pending (is Stats on and the video playing?)';
  video.removeAttribute('anaglyphohol-profile');
  video.setAttribute('anaglyphohol-profile-request', window.__profileMode || 'basic');
  window.__profilePending = true;
  return 'requested ' + (window.__profileMode || 'basic');
})()
