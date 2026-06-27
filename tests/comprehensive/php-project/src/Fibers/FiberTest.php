<?php

declare(strict_types=1);

namespace CompTest\Fibers;

final class FiberTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // Basic suspend/resume
        $fiber = new \Fiber(function(): string {
            $v1 = \Fiber::suspend('first_yield');
            $v2 = \Fiber::suspend('second_yield');
            return "done:$v1:$v2";
        });

        $y1 = $fiber->start();
        $r['fiber_first_yield']  = $y1 === 'first_yield';
        $r['fiber_not_terminated'] = !$fiber->isTerminated();

        $y2 = $fiber->resume('resume1');
        $r['fiber_second_yield'] = $y2 === 'second_yield';

        $fiber->resume('resume2');
        $r['fiber_terminated']   = $fiber->isTerminated();
        $r['fiber_return']       = $fiber->getReturn() === 'done:resume1:resume2';

        // Fiber passing values in
        $sum = 0;
        $adder = new \Fiber(function() use (&$sum): void {
            while (true) {
                $n = \Fiber::suspend();
                if ($n === null) break;
                $sum += $n;
            }
        });
        $adder->start();
        foreach ([10, 20, 30] as $n) {
            $adder->resume($n);
        }
        $adder->resume(null);
        $r['fiber_accumulate'] = $sum === 60;

        // Multiple independent fibers interleaving
        $log = [];
        $makeStep = function(string $name, int $steps) use (&$log): \Fiber {
            return new \Fiber(function() use ($name, $steps, &$log): void {
                for ($i = 1; $i <= $steps; $i++) {
                    $log[] = "$name:$i";
                    \Fiber::suspend();
                }
            });
        };

        $f1 = $makeStep('A', 3);
        $f2 = $makeStep('B', 2);
        $f1->start();
        $f2->start();
        while (!$f1->isTerminated() || !$f2->isTerminated()) {
            if (!$f1->isTerminated()) $f1->resume();
            if (!$f2->isTerminated()) $f2->resume();
        }
        $r['fiber_interleave'] = $log === ['A:1', 'B:1', 'A:2', 'B:2', 'A:3'];

        // Fiber::getCurrent() inside fiber
        $wasInsideFiber = false;
        $selfCheck = new \Fiber(function() use (&$wasInsideFiber): void {
            $wasInsideFiber = \Fiber::getCurrent() !== null;
            \Fiber::suspend();
        });
        $selfCheck->start();
        $r['fiber_get_current']  = $wasInsideFiber;
        $r['fiber_not_current_outside'] = \Fiber::getCurrent() === null;

        // Exception inside fiber propagates to resume()
        $errFiber = new \Fiber(function(): void {
            \Fiber::suspend();
            throw new \RuntimeException('fiber_error');
        });
        $errFiber->start();
        try {
            $errFiber->resume();
            $r['fiber_exception'] = false;
        } catch (\RuntimeException $e) {
            $r['fiber_exception'] = $e->getMessage() === 'fiber_error';
        }

        // Fiber as lazy evaluator (generates Fibonacci on demand)
        $fibGen = new \Fiber(function(): void {
            [$a, $b] = [0, 1];
            while (true) {
                \Fiber::suspend($a);
                [$a, $b] = [$b, $a + $b];
            }
        });
        $fibs = [];
        $val = $fibGen->start();
        for ($i = 0; $i < 8; $i++) {
            $fibs[] = $val;
            $val = $fibGen->resume();
        }
        $r['fiber_fibonacci'] = $fibs === [0, 1, 1, 2, 3, 5, 8, 13];

        return $r;
    }
}
