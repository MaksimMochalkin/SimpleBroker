// C3: hot account. All VUs withdraw 0.01 from ONE account; concurrency grows 10 -> 50 -> 200.
// k6 run -e BASE_URL=http://localhost:5000 -e MODE=atomic -e ACCOUNT=<id> loadtests/hot-account.js
// Needs the API started with Accounts__AllowModeOverride=true.
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { BASE, JSON_HDR } from './common.js';

const MODE = __ENV.MODE || 'atomic';
const ACC = __ENV.ACCOUNT || '1';
const conflicts = new Counter('conflicts_409');

http.setResponseCallback(http.expectedStatuses({ min: 200, max: 299 }, 409, 422));

export const options = {
  scenarios: {
    hot: {
      executor: 'ramping-vus',
      stages: [
        { target: 10, duration: '30s' },
        { target: 50, duration: '1m' },
        { target: 200, duration: '1m' },
        { target: 0, duration: '30s' },
      ],
    },
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  http.post(`${BASE}/accounts/${ACC}/deposit`, JSON.stringify({ amount: 1000000 }), JSON_HDR);
}

export default function () {
  const r = http.post(`${BASE}/accounts/${ACC}/withdraw?mode=${MODE}`, JSON.stringify({ amount: 0.01 }), JSON_HDR);
  if (r.status === 409) conflicts.add(1);
  check(r, { '200 or 409': (x) => x.status === 200 || x.status === 409 });
}
