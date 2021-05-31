using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FiguresDotStore.Controllers
{
	internal interface IRedisClient
	{
		int Get(string type);
		void Set(string type, int current);
	}

	public static class FiguresStorage
	{
		// корректно сконфигурированный и готовый к использованию клиент Редиса
		private static IRedisClient RedisClient { get; }

		public static bool CheckIfAvailable(string type, int count)
		{
			return RedisClient.Get(type) >= count;
		}

		public static void Reserve(string type, int count)
		{
			var current = RedisClient.Get(type);

			RedisClient.Set(type, current - count);
		}
	}

	public class Position
	{
		public string Type { get; set; }

		public float SideA { get; set; }
		public float SideB { get; set; }
		public float SideC { get; set; }

		public int Count { get; set; }
	}

	public class Cart
	{
		public List<Position> Positions { get; set; }
	}

	public class Order
	{
		public List<Figure> Positions { get; set; }

		public decimal GetTotal() =>
			Positions.Select(p => p switch
			{
				Triangle => (decimal)p.GetArea() * 1.2m,
				Circle => (decimal)p.GetArea() * 0.9m
			})
				.Sum();
	}

	public abstract class Figure
	{
		public float SideA { get; set; }
		public float SideB { get; set; }
		public float SideC { get; set; }

		public abstract void Validate();
		public abstract double GetArea();
	}

	public class Triangle : Figure
	{
		public override void Validate()
		{
			bool CheckTriangleInequality(float a, float b, float c) => a < b + c;
			if (CheckTriangleInequality(SideA, SideB, SideC)
				&& CheckTriangleInequality(SideB, SideA, SideC)
				&& CheckTriangleInequality(SideC, SideB, SideA))
				return;
			throw new InvalidOperationException("Triangle restrictions not met");
		}

		public override double GetArea()
		{
			var p = (SideA + SideB + SideC) / 2;
			return Math.Sqrt(p * (p - SideA) * (p - SideB) * (p - SideC));
		}

	}

	public class Square : Figure
	{
		public override void Validate()
		{
			if (SideA < 0)
				throw new InvalidOperationException("Square restrictions not met");

			if (SideA != SideB)
				throw new InvalidOperationException("Square restrictions not met");
		}

		public override double GetArea() => SideA * SideA;
	}

	public class Circle : Figure
	{
		public override void Validate()
		{
			if (SideA < 0)
				throw new InvalidOperationException("Circle restrictions not met");
		}

		public override double GetArea() => Math.PI * SideA * SideA;
	}

	public interface IOrderStorage
	{
		// сохраняет оформленный заказ и возвращает сумму
		Task<decimal> Save(Order order);
	}

	[ApiController]
	[Route("[controller]")]
	public class FiguresController : ControllerBase
	{
		private readonly ILogger<FiguresController> _logger;
		private readonly IOrderStorage _orderStorage;

		public FiguresController(ILogger<FiguresController> logger, IOrderStorage orderStorage)
		{
			_logger = logger;
			_orderStorage = orderStorage;
		}

		// хотим оформить заказ и получить в ответе его стоимость
		[HttpPost]
		public async Task<ActionResult> Order(Cart cart)
		{
			foreach (var position in cart.Positions)
			{
				if (!FiguresStorage.CheckIfAvailable(position.Type, position.Count))
				{
					return new BadRequestResult();
				}
			}

			var order = new Order
			{
				Positions = cart.Positions.Select(p =>
				{
					Figure figure = p.Type switch
					{
						"Circle" => new Circle(),
						"Triangle" => new Triangle(),
						"Square" => new Square()
					};
					figure.SideA = p.SideA;
					figure.SideB = p.SideB;
					figure.SideC = p.SideC;
					figure.Validate();
					return figure;
				}).ToList()
			};

			foreach (var position in cart.Positions)
			{
				FiguresStorage.Reserve(position.Type, position.Count);
			}

			var result = _orderStorage.Save(order);

			return new OkObjectResult(result.Result);
		}
	}
}
