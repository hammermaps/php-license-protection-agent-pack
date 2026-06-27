<?php

declare(strict_types=1);

namespace CompTest\Oop;

use CompTest\Oop\Contracts\Shape;
use CompTest\Oop\Traits\Loggable;

final class Circle implements Shape
{
    use Loggable;

    public function __construct(private readonly float $radius)
    {
        $this->logEvent("Circle(r={$radius}) constructed");
    }

    public function area(): float
    {
        return M_PI * $this->radius ** 2;
    }

    public function perimeter(): float
    {
        return 2 * M_PI * $this->radius;
    }

    public function describe(): string
    {
        return sprintf('Circle r=%.2f area=%.4f peri=%.4f', $this->radius, $this->area(), $this->perimeter());
    }
}
