<?php

declare(strict_types=1);

namespace CompTest\Oop;

use CompTest\Oop\Contracts\Shape;

final class OopTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $results = [];

        // Interface + Trait on Circle
        $c = new Circle(5.0);
        $results['circle_area'] = abs($c->area() - M_PI * 25) < 1e-9;
        $results['circle_peri'] = abs($c->perimeter() - 2 * M_PI * 5) < 1e-9;
        $results['circle_log']  = count($c->getLog()) === 1;

        // Interface + Trait on Rectangle
        $r = new Rectangle(4.0, 6.0);
        $results['rect_area']   = $r->area() === 24.0;
        $results['rect_peri']   = $r->perimeter() === 20.0;

        // Polymorphism via interface
        $shapes = [$c, $r];
        $totalArea = array_reduce(
            $shapes,
            fn(float $carry, Shape $s) => $carry + $s->area(),
            0.0
        );
        $results['polymorphism'] = $totalArea > 0;

        // Readonly properties
        $results['readonly_prop'] = $this->testReadonly();

        // Named arguments
        $results['named_args'] = $this->testNamedArgs();

        // Enum (PHP 8.1+)
        $results['enum'] = $this->testEnum();

        // First-class callables
        $results['first_class_callable'] = $this->testFirstClassCallable();

        return $results;
    }

    private function testReadonly(): bool
    {
        $obj = new readonly class(42, 'hello') {
            public function __construct(
                public int $n,
                public string $s,
            ) {}
        };
        return $obj->n === 42 && $obj->s === 'hello';
    }

    private function testNamedArgs(): bool
    {
        $arr = array_slice(array: [1, 2, 3, 4, 5], offset: 1, length: 3);
        return $arr === [2, 3, 4];
    }

    private function testEnum(): bool
    {
        $status = TestStatus::Active;
        return $status->label() === 'active' && $status->value === 1;
    }

    private function testFirstClassCallable(): bool
    {
        $fn = strlen(...);
        return $fn('hello') === 5;
    }
}

enum TestStatus: int
{
    case Active   = 1;
    case Inactive = 0;

    public function label(): string
    {
        return match($this) {
            self::Active   => 'active',
            self::Inactive => 'inactive',
        };
    }
}
