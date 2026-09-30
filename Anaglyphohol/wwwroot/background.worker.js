// Script loader works in workers
importScripts(chrome.runtime.getURL('app/background.js'));
importScripts(chrome.runtime.getURL('app/main.classic.js'));
