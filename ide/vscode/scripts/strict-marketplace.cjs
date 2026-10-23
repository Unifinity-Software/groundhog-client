// Preload only in the Marketplace publisher. The SDK otherwise resolves HTTP 404 uploads.
const { RestClient } = require('typed-rest-client/RestClient');
const upload = RestClient.prototype.uploadStream;
RestClient.prototype.uploadStream = async function (verb, ...args) {
  const method = /^(POST|PUT|PATCH)$/.test(verb) ? verb : 'UPLOAD';
  let status;
  try {
    const response = await upload.call(this, verb, ...args);
    status = response.statusCode;
    if (!Number.isInteger(status) || status < 200 || status > 299) throw new Error();
    console.log(`Marketplace upload ${method} HTTP ${status}`);
    return response;
  } catch (error) {
    status ??= error?.statusCode;
    const safeStatus = Number.isInteger(status) && status >= 100 && status <= 599 ? status : 'unknown';
    // Do not forward SDK messages, response bodies, headers or signed URLs.
    console.log(`Marketplace upload ${method} HTTP ${safeStatus}`);
    const sanitized = new Error(`Marketplace upload ${method} failed (HTTP ${safeStatus}).`);
    if (safeStatus !== 'unknown') sanitized.statusCode = safeStatus; // Preserve vsce's 409 duplicate handling.
    throw sanitized;
  }
};
