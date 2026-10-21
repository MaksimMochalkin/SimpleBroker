// L3: writes only. 100 -> 200 -> 400 rps, 2 min each.
// k6 run -e BASE_URL=http://localhost:5000 loadtests/orders-write.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, JSON_HDR, pickAccount, orderBody, is2xx } from './common.js';

const step = (rate) => [
  { target: rate, duration: '10s' },
  { target: rate, duration: '110s' },
];

export const options = {
  scenarios: {
    writes: {
      executor: 'ramping-arrival-rate',
      startRate: 100, timeUnit: '1s', preAllocatedVUs: 200, maxVUs: 1000,
      stages: [...step(100), ...step(200), ...step(400)],
    },
  },
  thresholds: { http_req_failed: ['rate<0.01'] },
};

export default function () {
  const res = http.post(`${BASE}/orders`, orderBody(pickAccount()), JSON_HDR);
  check(res, { 'status 201': (r) => r.status === 201 });
}
