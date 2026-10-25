dotnet --version
10.0.402

docker stats
CONTAINER ID   NAME                             CPU %     MEM USAGE / LIMIT     MEM %     NET I/O           BLOCK I/O         PIDS
b59fda02be8f   simple-broker-otel-collector-1   0.13%     40.46MiB / 15.38GiB   0.26%     21MB / 3.49MB     68.6MB / 0B       15
257b5f5d9d61   simple-broker-postgres-1         0.00%     401.8MiB / 4GiB       9.81%     760MB / 12.1MB    763MB / 3.67GB    106
43443a5fa7f6   simple-broker-prometheus-1       0.00%     25.82MiB / 15.38GiB   0.16%     751kB / 240kB     50.1MB / 655kB    15
832fb7cb773f   simple-broker-grafana-1          2.33%     55.22MiB / 15.38GiB   0.35%     14.7MB / 595kB    26.9MB / 48.1MB   22
6489e0cb2475   simple-broker-jaeger-1           0.03%     182.4MiB / 15.38GiB   1.16%     2.64MB / 57.2kB   7.21MB / 0B       25

docker system df
docker system df
TYPE            TOTAL     ACTIVE    SIZE      RECLAIMABLE
Images          9         7         4.839GB   29.07MB (0%)
Containers      7         5         318.5MB   318.4MB (99%)
Local Volumes   4         4         1.74GB    0B (0%)
Build Cache     0         0         0B        0B

первый, масштаб М

k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js

         /\      Grafana   /‾‾/
    /\  /  \     |\  __   /  /
   /  \/    \    | |/ /  /   ‾‾\
  /          \   |   (  |  (‾)  |
 / __________ \  |_|\_\  \_____/


     execution: local
        script: loadtests/baseline.js
        output: -

     scenarios: (100.00%) 1 scenario, 500 max VUs, 7m0s max duration (incl. graceful stop):
              * mixed: Up to 300.00 iterations/s for 6m30s over 4 stages (maxVUs: 100-500, gracefulStop: 30s)

WARN[0031] Insufficient VUs, reached 500 active VUs and cannot initialize more  executor=ramping-arrival-rate scenario=mixed
WARN[0378] The test has generated metrics with 100034 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester


  █ THRESHOLDS

    http_req_duration
    ✗ 'p(95)<1000' p(95)=22.69s

    http_req_failed
    ✓ 'rate<0.01' rate=0.00%


  █ TOTAL RESULTS

    checks_total.......: 16187   40.784174/s
    checks_succeeded...: 100.00% 16187 out of 16187
    checks_failed......: 0.00%   0 out of 16187

    ✓ status 2xx

    HTTP
    http_req_duration..............: avg=11.6s min=514.8µs med=9.64s max=30.69s p(90)=21.66s p(95)=22.69s
      { expected_response:true }...: avg=11.6s min=514.8µs med=9.64s max=30.69s p(90)=21.66s p(95)=22.69s
    http_req_failed................: 0.00%  0 out of 16187
    http_reqs......................: 16187  40.784174/s

    EXECUTION
    dropped_iterations.............: 64812  163.297949/s
    iteration_duration.............: avg=11.6s min=520.2µs med=9.64s max=30.69s p(90)=21.66s p(95)=22.69s
    iterations.....................: 16187  40.784174/s
    vus............................: 114    min=0          max=500
    vus_max........................: 500    min=100        max=500

    NETWORK
    data_received..................: 5.2 MB 13 kB/s
    data_sent......................: 2.1 MB 5.2 kB/s




running (6m36.9s), 000/500 VUs, 16187 complete and 0 interrupted iterations
mixed ✓ [======================================] 000/500 VUs  6m30s  002.24 iters/s
ERRO[0397] thresholds on metrics 'http_req_duration' have been crossed

На момент завершения k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js
Press p to pause, r to resume, q to quit.
    Status: Paused

Name                                                                                                       Current Value
[System.Runtime]
    dotnet.assembly.count ({assembly})                                                                           139
    dotnet.exceptions ({exception})
        error.type
        ------------------------
        ConnectionResetException                                                                               1,000        dotnet.gc.collections ({collection})
        gc.heap.generation
        ------------------
        gen0                                                                                                      15
        gen1                                                                                                       2
        gen2                                                                                                       3
    dotnet.gc.heap.total_allocated (By)                                                                       5.9276e+08
    dotnet.gc.last_collection.heap.fragmentation.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                                 520,792
        gen2                                                                                               3,388,824
        loh                                                                                                    1,728
        poh                                                                                                    4,216
    dotnet.gc.last_collection.heap.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                              33,276,544    



Второй прогон на масштабе М
k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js

         /\      Grafana   /‾‾/
    /\  /  \     |\  __   /  /
   /  \/    \    | |/ /  /   ‾‾\
  /          \   |   (  |  (‾)  |
 / __________ \  |_|\_\  \_____/


     execution: local
        script: loadtests/baseline.js
        output: -

     scenarios: (100.00%) 1 scenario, 500 max VUs, 7m0s max duration (incl. graceful stop):
              * mixed: Up to 300.00 iterations/s for 6m30s over 4 stages (maxVUs: 100-500, gracefulStop: 30s)

WARN[0035] Insufficient VUs, reached 500 active VUs and cannot initialize more  executor=ramping-arrival-rate scenario=mixed
WARN[0374] The test has generated metrics with 100007 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester


  █ THRESHOLDS

    http_req_duration
    ✗ 'p(95)<1000' p(95)=22.57s

    http_req_failed
    ✓ 'rate<0.01' rate=0.00%


  █ TOTAL RESULTS

    checks_total.......: 16327   41.121842/s
    checks_succeeded...: 100.00% 16327 out of 16327
    checks_failed......: 0.00%   0 out of 16327

    ✓ status 2xx

    HTTP
    http_req_duration..............: avg=11.38s min=502µs   med=9.67s max=26.23s p(90)=21.49s p(95)=22.57s
      { expected_response:true }...: avg=11.38s min=502µs   med=9.67s max=26.23s p(90)=21.49s p(95)=22.57s
    http_req_failed................: 0.00%  0 out of 16327
    http_reqs......................: 16327  41.121842/s

    EXECUTION
    dropped_iterations.............: 64672  162.885514/s
    iteration_duration.............: avg=11.38s min=513.9µs med=9.67s max=26.23s p(90)=21.49s p(95)=22.57s
    iterations.....................: 16327  41.121842/s
    vus............................: 9      min=0          max=500
    vus_max........................: 500    min=100        max=500

    NETWORK
    data_received..................: 5.0 MB 13 kB/s
    data_sent......................: 2.1 MB 5.3 kB/s




running (6m37.0s), 000/500 VUs, 16327 complete and 0 interrupted iterations
mixed ✓ [======================================] 000/500 VUs  6m30s  002.24 iters/s
ERRO[0397] thresholds on metrics 'http_req_duration' have been crossed

На момент завершения k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js
Press p to pause, r to resume, q to quit.
    Status: Paused

Name                                                                                                       Current Value
[System.Runtime]
    dotnet.assembly.count ({assembly})                                                                           139
    dotnet.exceptions ({exception})
        error.type
        --------------------
        EndOfStreamException                                                                                     172            NpgsqlException                                                                                          344        dotnet.gc.collections ({collection})
        gc.heap.generation
        ------------------
        gen0                                                                                                      23            gen1                                                                                                       2            gen2                                                                                                       3        dotnet.gc.heap.total_allocated (By)                                                                       1.1251e+09
    dotnet.gc.last_collection.heap.fragmentation.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                               1,277,024
        gen2                                                                                               3,388,824
        loh                                                                                                    1,728
        poh                                                                                                    4,216
    dotnet.gc.last_collection.heap.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0  


Третий прогон на масштабе М
 k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js

         /\      Grafana   /‾‾/
    /\  /  \     |\  __   /  /
   /  \/    \    | |/ /  /   ‾‾\
  /          \   |   (  |  (‾)  |
 / __________ \  |_|\_\  \_____/


     execution: local
        script: loadtests/baseline.js
        output: -

     scenarios: (100.00%) 1 scenario, 500 max VUs, 7m0s max duration (incl. graceful stop):
              * mixed: Up to 300.00 iterations/s for 6m30s over 4 stages (maxVUs: 100-500, gracefulStop: 30s)

WARN[0038] Insufficient VUs, reached 500 active VUs and cannot initialize more  executor=ramping-arrival-rate scenario=mixed
WARN[0359] The test has generated metrics with 100016 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester


  █ THRESHOLDS

    http_req_duration
    ✗ 'p(95)<1000' p(95)=21.99s

    http_req_failed
    ✓ 'rate<0.01' rate=0.00%


  █ TOTAL RESULTS

    checks_total.......: 16811   42.377021/s
    checks_succeeded...: 100.00% 16811 out of 16811
    checks_failed......: 0.00%   0 out of 16811

    ✓ status 2xx

    HTTP
    http_req_duration..............: avg=11.01s min=504.9µs med=9.28s max=25.88s p(90)=21.28s p(95)=21.99s
      { expected_response:true }...: avg=11.01s min=504.9µs med=9.28s max=25.88s p(90)=21.28s p(95)=21.99s
    http_req_failed................: 0.00%  0 out of 16811
    http_reqs......................: 16811  42.377021/s

    EXECUTION
    dropped_iterations.............: 64188  161.804547/s
    iteration_duration.............: avg=11.01s min=815.3µs med=9.28s max=25.88s p(90)=21.28s p(95)=21.99s
    iterations.....................: 16811  42.377021/s
    vus............................: 65     min=9          max=500
    vus_max........................: 500    min=100        max=500

    NETWORK
    data_received..................: 5.2 MB 13 kB/s
    data_sent......................: 2.1 MB 5.4 kB/s




running (6m36.7s), 000/500 VUs, 16811 complete and 0 interrupted iterations
mixed ✓ [======================================] 000/500 VUs  6m30s  002.24 iters/s
ERRO[0397] thresholds on metrics 'http_req_duration' have been crossed   

На момент завершения k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js
Press p to pause, r to resume, q to quit.
    Status: Paused

Name                                                                                                       Current Value
[System.Runtime]
    dotnet.assembly.count ({assembly})                                                                           139
    dotnet.exceptions ({exception})
        error.type
        ------------------------
        ConnectionResetException                                                                               1,000        dotnet.gc.collections ({collection})
        gc.heap.generation
        ------------------
        gen0                                                                                                      31
        gen1                                                                                                       2
        gen2                                                                                                       4
    dotnet.gc.heap.total_allocated (By)                                                                       1.6469e+09
    dotnet.gc.last_collection.heap.fragmentation.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                              98,819,288
        gen2                                                                                               9,331,184
        loh                                                                                                2,098,928
        poh                                                                                                3,930,632
    dotnet.gc.last_collection.heap.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                                  1.1848e+08



Первый прогон на масштабе S
 k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js

         /\      Grafana   /‾‾/
    /\  /  \     |\  __   /  /
   /  \/    \    | |/ /  /   ‾‾\
  /          \   |   (  |  (‾)  |
 / __________ \  |_|\_\  \_____/


     execution: local
        script: loadtests/baseline.js
        output: -

     scenarios: (100.00%) 1 scenario, 500 max VUs, 7m0s max duration (incl. graceful stop):
              * mixed: Up to 300.00 iterations/s for 6m30s over 4 stages (maxVUs: 100-500, gracefulStop: 30s)

WARN[0142] The test has generated metrics with 100024 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester
WARN[0232] The test has generated metrics with 200006 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester


  █ THRESHOLDS

    http_req_duration
    ✓ 'p(95)<1000' p(95)=18.77ms

    http_req_failed
    ✓ 'rate<0.01' rate=0.00%


  █ TOTAL RESULTS

    checks_total.......: 80849   207.298094/s
    checks_succeeded...: 100.00% 80849 out of 80849
    checks_failed......: 0.00%   0 out of 80849

    ✓ status 2xx

    HTTP
    http_req_duration..............: avg=22.89ms min=0s med=6.15ms max=4.06s p(90)=10.38ms p(95)=18.77ms
      { expected_response:true }...: avg=22.89ms min=0s med=6.15ms max=4.06s p(90)=10.38ms p(95)=18.77ms
    http_req_failed................: 0.00% 0 out of 80849
    http_reqs......................: 80849 207.298094/s

    EXECUTION
    dropped_iterations.............: 151   0.387166/s
    iteration_duration.............: avg=23.04ms min=0s med=6.32ms max=4.06s p(90)=10.49ms p(95)=19.06ms
    iterations.....................: 80849 207.298094/s
    vus............................: 0     min=0          max=203
    vus_max........................: 222   min=100        max=222

    NETWORK
    data_received..................: 26 MB 66 kB/s
    data_sent......................: 10 MB 26 kB/s




running (6m30.0s), 000/222 VUs, 80849 complete and 0 interrupted iterations
mixed ✓ [======================================] 000/222 VUs  6m30s  002.24 iters/s

На момент завершения k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js
Press p to pause, r to resume, q to quit.
    Status: Paused

Name                                                                                                       Current Value
[System.Runtime]
    dotnet.assembly.count ({assembly})                                                                           139
    dotnet.exceptions ({exception})
        error.type
        ------------------------
        ConnectionResetException                                                                                 444            EndOfStreamException                                                                                     198            NpgsqlException                                                                                          396        dotnet.gc.collections ({collection})
        gc.heap.generation
        ------------------
        gen0                                                                                                      65
        gen1                                                                                                       7
        gen2                                                                                                       4
    dotnet.gc.heap.total_allocated (By)                                                                       3.9732e+09
    dotnet.gc.last_collection.heap.fragmentation.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                                  28,200
        gen2                                                                                               4,063,224
        loh                                                                                                2,098,928
        poh                                                                                                2,039,552
    dotnet.gc.last_collection.heap.size (By)
        gc.heap.generation
        ------------------                                                                                            


Второй прогон на масштабе S
k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js

         /\      Grafana   /‾‾/
    /\  /  \     |\  __   /  /
   /  \/    \    | |/ /  /   ‾‾\
  /          \   |   (  |  (‾)  |
 / __________ \  |_|\_\  \_____/


     execution: local
        script: loadtests/baseline.js
        output: -

     scenarios: (100.00%) 1 scenario, 500 max VUs, 7m0s max duration (incl. graceful stop):
              * mixed: Up to 300.00 iterations/s for 6m30s over 4 stages (maxVUs: 100-500, gracefulStop: 30s)

WARN[0142] The test has generated metrics with 100015 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester
WARN[0231] The test has generated metrics with 200023 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester


  █ THRESHOLDS

    http_req_duration
    ✓ 'p(95)<1000' p(95)=10.4ms

    http_req_failed
    ✓ 'rate<0.01' rate=0.00%


  █ TOTAL RESULTS

    checks_total.......: 81000   207.683389/s
    checks_succeeded...: 100.00% 81000 out of 81000
    checks_failed......: 0.00%   0 out of 81000

    ✓ status 2xx

    HTTP
    http_req_duration..............: avg=5.57ms min=0s med=5.59ms max=266.97ms p(90)=8.98ms p(95)=10.4ms
      { expected_response:true }...: avg=5.57ms min=0s med=5.59ms max=266.97ms p(90)=8.98ms p(95)=10.4ms
    http_req_failed................: 0.00% 0 out of 81000
    http_reqs......................: 81000 207.683389/s

    EXECUTION
    iteration_duration.............: avg=5.77ms min=0s med=5.85ms max=266.96ms p(90)=9.13ms p(95)=10.58ms
    iterations.....................: 81000 207.683389/s
    vus............................: 0     min=0          max=9
    vus_max........................: 100   min=100        max=100

    NETWORK
    data_received..................: 25 MB 64 kB/s
    data_sent......................: 10 MB 26 kB/s




running (6m30.0s), 000/100 VUs, 81000 complete and 0 interrupted iterations
mixed ✓ [======================================] 000/100 VUs  6m30s  002.24 iters/s

На момент завершения k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js
Press p to pause, r to resume, q to quit.
    Status: Paused

Name                                                                                                       Current Value
[System.Runtime]
    dotnet.assembly.count ({assembly})                                                                           139
    dotnet.exceptions ({exception})
        error.type
        ------------------------
        ConnectionResetException                                                                                 200            EndOfStreamException                                                                                      50            NpgsqlException                                                                                          100        dotnet.gc.collections ({collection})
        gc.heap.generation
        ------------------
        gen0                                                                                                      73
        gen1                                                                                                      41            gen2                                                                                                       4        dotnet.gc.heap.total_allocated (By)                                                                       6.2833e+09
    dotnet.gc.last_collection.heap.fragmentation.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                                  26,424
        gen2                                                                                               2,325,072
        loh                                                                                                2,098,928
        poh                                                                                                  346,232
    dotnet.gc.last_collection.heap.size (By)
        gc.heap.generation
        ------------------                                                                                              


Третий прогон на масштабе S
k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js

         /\      Grafana   /‾‾/
    /\  /  \     |\  __   /  /
   /  \/    \    | |/ /  /   ‾‾\
  /          \   |   (  |  (‾)  |
 / __________ \  |_|\_\  \_____/


     execution: local
        script: loadtests/baseline.js
        output: -

     scenarios: (100.00%) 1 scenario, 500 max VUs, 7m0s max duration (incl. graceful stop):
              * mixed: Up to 300.00 iterations/s for 6m30s over 4 stages (maxVUs: 100-500, gracefulStop: 30s)

WARN[0142] The test has generated metrics with 100051 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester
WARN[0229] The test has generated metrics with 200104 unique time series, which is higher than the suggested limit of 100000 and could cause high memory usage. Consider not using high-cardinality values like unique IDs as metric tags or, if you need them in the URL, use the name metric tag or URL grouping. See https://grafana.com/docs/k6/latest/using-k6/tags-and-groups/ for details.  component=metrics-engine-ingester


  █ THRESHOLDS

    http_req_duration
    ✓ 'p(95)<1000' p(95)=10.06ms

    http_req_failed
    ✓ 'rate<0.01' rate=0.00%


  █ TOTAL RESULTS

    checks_total.......: 80999   207.686844/s
    checks_succeeded...: 100.00% 80999 out of 80999
    checks_failed......: 0.00%   0 out of 80999

    ✓ status 2xx

    HTTP
    http_req_duration..............: avg=6.92ms min=0s med=5.07ms max=1.56s p(90)=8.55ms p(95)=10.06ms
      { expected_response:true }...: avg=6.92ms min=0s med=5.07ms max=1.56s p(90)=8.55ms p(95)=10.06ms
    http_req_failed................: 0.00% 0 out of 80999
    http_reqs......................: 80999 207.686844/s

    EXECUTION
    iteration_duration.............: avg=7.09ms min=0s med=5.26ms max=1.56s p(90)=8.65ms p(95)=10.2ms
    iterations.....................: 80999 207.686844/s
    vus............................: 0     min=0          max=80
    vus_max........................: 100   min=100        max=100

    NETWORK
    data_received..................: 25 MB 63 kB/s
    data_sent......................: 10 MB 26 kB/s




running (6m30.0s), 000/100 VUs, 80999 complete and 0 interrupted iterations
mixed ✓ [======================================] 000/100 VUs  6m30s  002.24 iters/s

На момент завершения k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js
Press p to pause, r to resume, q to quit.
    Status: Paused

Name                                                                                                       Current Value
[System.Runtime]
    dotnet.assembly.count ({assembly})                                                                           139
    dotnet.exceptions ({exception})
        error.type
        ------------------------
        ConnectionResetException                                                                                 200            EndOfStreamException                                                                                     190            NpgsqlException                                                                                          380        dotnet.gc.collections ({collection})
        gc.heap.generation
        ------------------
        gen0                                                                                                      92
        gen1                                                                                                      63
        gen2                                                                                                       5
    dotnet.gc.heap.total_allocated (By)                                                                       8.6003e+09
    dotnet.gc.last_collection.heap.fragmentation.size (By)
        gc.heap.generation
        ------------------
        gen0                                                                                                       0
        gen1                                                                                                  28,368
        gen2                                                                                               8,776,456
        loh                                                                                                    1,728
        poh                                                                                                3,955,352
    dotnet.gc.last_collection.heap.size (By)
        gc.heap.generation
        ------------------                                                                                              