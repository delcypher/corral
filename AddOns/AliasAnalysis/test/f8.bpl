var M1: [int] int;
var M2: [int] int;

function {:aliasingQuery "allocationsites"} f1(a:int): bool;
function {:aliasingQuery "allocationsites"} f2(a:int): bool;
function {:aliasingQuery "allocationsites"} f3(a:int): bool;

procedure {:allocator "full"} malloc_full() returns (x:int);
procedure {:allocator} malloc() returns (x:int);

procedure foo() 
{
   var a, b, c, d, t1, t2, t3: int; 

   call a := malloc();
   call b := bar();
   call c := baz();

   assume f1(a);
   assume f2(b);
   assume f3(c);

   assert a != b;
}

procedure bar() returns (x:int)
{
  call x := malloc();
}

procedure baz() returns (x:int)
{
  call x := malloc();
}
