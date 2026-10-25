// L2: trades read at constant 200 rps, page uniformly in 1..50. Look at p95 per page (tag "page").
// k6 run -e BASE_URL=http://localhost:5000 loadtests/trades-read.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, pickAccount, randInt, is2xx } from './common.js';

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    trades: {
      executor: 'constant-arrival-rate',
      rate: 200, timeUnit: '1s', duration: '3m', preAllocatedVUs: 100, maxVUs: 400,
    },
  },
  thresholds: { http_req_failed: ['rate<0.01'] },
};

export default function () {
  const page = 1 + randInt(50);
  const res = http.get(`${BASE}/accounts/${pickAccount()}/trades?page=${page}&size=50`, {
    tags: { page: String(page) },
  });
  check(res, { 'status 2xx': is2xx });
}
