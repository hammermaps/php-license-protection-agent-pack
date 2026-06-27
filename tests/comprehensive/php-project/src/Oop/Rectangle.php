<?php

declare(strict_types=1);

namespace CompTest\Oop;

use CompTest\Oop\Contracts\Shape;
use CompTest\Oop\Traits\Loggable;

final class Rectangle implements Shape
{
    use Loggable;

    public function __construct(
        private readonly float $width,
        private readonly float $height,
    ) {
        $this->logEvent("Rectangle({$width}x{$height}) constructed");
    }

    public function area(): float
    {
        return $this->width * $this->height;
    }

    public function perimeter(): float
    {
        return 2 * ($this->width + $this->height);
    }

    public function describe(): string
    {
        return sprintf('Rectangle %gx%g area=%.4f peri=%.4f',
            $this->width, $this->height, $this->area(), $this->perimeter());
    }
}
