// L1: baseline mix. 20% POST /orders, 50% GET trades, 30% GET balance.
// k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, JSON_HDR, pickAccount, orderBody, randInt, is2xx } from './common.js';

export const options = {
  scenarios: {
    mixed: {
      executor: 'ramping-arrival-rate',
      startRate: 50, timeUnit: '1s', preAllocatedVUs: 100, maxVUs: 500,
      stages: [
        { target: 100, duration: '1m' },
        { target: 300, duration: '3m' },
        { target: 300, duration: '2m' },
        { target: 0, duration: '30s' },
      ],
    },
  },
  thresholds: { http_req_failed: ['rate<0.01'], http_req_duration: ['p(95)<1000'] },
};

export default function () {
  const id = pickAccount();
  const r = Math.random();
  let res;
  if (r < 0.2) {
    res = http.post(`${BASE}/orders`, orderBody(id), JSON_HDR);
  } else if (r < 0.7) {
    res = http.get(`${BASE}/accounts/${id}/trades?page=${1 + randInt(20)}&size=50`);
  } else {
    res = http.get(`${BASE}/accounts/${id}/balance`);
  }
  check(res, { 'status 2xx': is2xx });
}
