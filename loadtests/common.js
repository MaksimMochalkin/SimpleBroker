import { SharedArray } from 'k6/data';

export const accounts = new SharedArray('accounts', () => JSON.parse(open('./data/accounts.json')));
export const BASE = __ENV.BASE_URL || 'http://localhost:5000';
export const JSON_HDR = { headers: { 'Content-Type': 'application/json' } };
export const INSTRUMENTS = parseInt(__ENV.INSTRUMENTS || '100', 10); // S and M both have >= 100

export const randInt = (n) => Math.floor(Math.random() * n);
export const pickAccount = () => accounts[randInt(accounts.length)];

export function orderBody(accountId) {
  return JSON.stringify({
    accountId,
    instrumentId: 1 + randInt(INSTRUMENTS),
    side: Math.random() < 0.5 ? 'Buy' : 'Sell',
    type: 'Limit',
    price: 100 + Math.random() * 10,
    quantity: 1 + randInt(100),
  });
}

export const is2xx = (r) => r.status >= 200 && r.status < 300;
