import http from 'k6/http';
import { SharedArray } from 'k6/data';
import { Counter } from 'k6/metrics';

export const accounts = new SharedArray('accounts', () => JSON.parse(open('./data/accounts.json')));
export const BASE = __ENV.BASE_URL || 'http://localhost:5000';
export const JSON_HDR = { headers: { 'Content-Type': 'application/json' } };
export const INSTRUMENTS = parseInt(__ENV.INSTRUMENTS || '100', 10); // S and M both have >= 100

// Stage 2: Buy orders reserve funds, so 422 (insufficient funds) is a legitimate answer, not an error.
export const insufficientFunds = new Counter('insufficient_funds');
http.setResponseCallback(http.expectedStatuses({ min: 200, max: 299 }, 422));

export const randInt = (n) => Math.floor(Math.random() * n);
export const pickAccount = () => accounts[randInt(accounts.length)];

export function orderBody(accountId) {
  return JSON.stringify({
    accountId,
    instrumentId: 1 + randInt(INSTRUMENTS),
    side: Math.random() < 0.5 ? 'Buy' : 'Sell',
    type: 'Limit',
    price: 100 + Math.random() * 10,
    quantity: 1 + randInt(10), // stage 2: max reserve ~1100 per order (was 1..100)
  });
}

export const is2xx = (r) => r.status >= 200 && r.status < 300;