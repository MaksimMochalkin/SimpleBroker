// L4: step stress, +100 rps every 2 min up to 1500 rps; aborts when the system degrades.
// k6 run -e BASE_URL=http://localhost:5000 loadtests/stress-step.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, JSON_HDR, pickAccount, orderBody, randInt, is2xx } from './common.js';

const stages = [];
for (let rate = 100; rate <= 1500; rate += 100) {
  stages.push({ target: rate, duration: '10s' }, { target: rate, duration: '110s' });
}

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    step: {
      executor: 'ramping-arrival-rate',
      startRate: 100, timeUnit: '1s', preAllocatedVUs: 300, maxVUs: 3000,
      stages,
    },
  },
  thresholds: {
    http_req_failed: [{ threshold: 'rate<0.05', abortOnFail: true, delayAbortEval: '30s' }],
    http_req_duration: [{ threshold: 'p(95)<3000', abortOnFail: true, delayAbortEval: '30s' }],
  },
};

export default function () {
  const id = pickAccount();
  const r = Math.random();
  let res;
  if (r < 0.2) res = http.post(`${BASE}/orders`, orderBody(id), JSON_HDR);
  else if (r < 0.7) res = http.get(`${BASE}/accounts/${id}/trades?page=${1 + randInt(20)}&size=50`);
  else res = http.get(`${BASE}/accounts/${id}/balance`);
  check(res, { 'status 2xx': is2xx });
}
