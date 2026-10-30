// L2: trades read at constant 200 rps. Set CURSOR to benchmark a later keyset page.
// k6 run -e BASE_URL=http://localhost:5000 loadtests/trades-read.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, pickAccount, is2xx } from './common.js';
const rate = parseInt(__ENV.RATE || '200', 10);
const engine = __ENV.ENGINE || 'ef';
if (engine !== 'ef' && engine !== 'dapper') {
  throw new Error(`Unsupported ENGINE '${engine}'; expected 'ef' or 'dapper'.`);
}
const enginePath = engine === 'dapper' ? '/dapper' : '';

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    trades: {
      executor: 'constant-arrival-rate',
      rate, timeUnit: '1s', duration: '3m', preAllocatedVUs: 100, maxVUs: 400,
    },
  },
  thresholds: { http_req_failed: ['rate<0.01'] },
};

export default function () {
  const cursor = __ENV.CURSOR ? `&cursor=${encodeURIComponent(__ENV.CURSOR)}` : '';
  const page = __ENV.PAGE_LABEL || '1';
  const account = __ENV.ACCOUNT_ID || pickAccount();
  const res = http.get(`${BASE}/accounts/${account}/trades${enginePath}?size=50${cursor}`, {
    tags: { engine, page },
  });
  check(res, { 'status 2xx': is2xx });
}
