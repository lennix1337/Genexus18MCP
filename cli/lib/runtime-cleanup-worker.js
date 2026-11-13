'use strict';

// Worker-thread entry for startRuntimeCleanupInBackground (runtime-stager.js).
const { parentPort, workerData } = require('worker_threads');
const { runRuntimeCleanup } = require('./runtime-stager');

parentPort.postMessage(runRuntimeCleanup(workerData));
