using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
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
		//пердполагаем, что в пустой корзине смысла нет
		[NotNull]
        [Required]
		public List<Position> Positions { get; set; }
	}

	public class Order
	{
		[NotNull]
		[Required]
		public List<Figure> Positions { get; set; }

		public decimal GetTotal() =>
			Positions.Select(p => p switch
			{
                Triangle _ => (decimal)p.GetArea() * 1.2m,
                Circle _ => (decimal)p.GetArea() * 0.9m,
                _ => throw new ArgumentException("Invalid type")
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
            //тут не хватало tolerance. вроде мы говорим о геометрии и сравнении фигур, скорей всего это важно. 
			if (Math.Abs(SideA - SideB) > 5)
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
        private readonly object _lockobject = new object();

        public FiguresController(ILogger<FiguresController> logger, IOrderStorage orderStorage)
		{
			_logger = logger;
			_orderStorage = orderStorage;
		}

		// хотим оформить заказ и получить в ответе его стоимость
		[HttpPost]
		public async Task<ActionResult> Order(Cart cart)
        {
            //валидируем, чтобы не прислали нули. т.к. поставили dbannotation, должно работать.
            if (!TryValidateModel(cart)) return BadRequest();
            //проверяем недоступные позиции
            var notAvailablePositions =
                cart.Positions.Where(position => !FiguresStorage.CheckIfAvailable(position.Type, position.Count)).ToList();
			if (notAvailablePositions.Count > 0)
            {
                //предполагаем, что мы вообще не создаем ордер, если есть недоступные позиции, есть конечно вариант, что можно создавать не только из доступных, но без тз - не ясно.
                //возвращаем заодно клиенту, то - что недоступно, может в интерфейсе отобразят
				return new NotFoundObjectResult(notAvailablePositions);
			}
            
			var order = new Order
			{
				//переписал на test type pattern. вроде версия .net 5 c# 9, но не работало
                Positions = cart.Positions.Select(p =>
				{
                    var figure = p.Type switch
                    {
                        "Circle" =>(Figure) new Circle(),
                        "Triangle" => new Triangle(),
                        "Square" => new Square(),
                        _ => null
                    };

                    if (figure == null) return null;
                    figure.SideA = p.SideA;
                    figure.SideB = p.SideB;
                    figure.SideC = p.SideC;
                    try
                    {
						//т.к. validate возвращает excepшны то надо чекнуть и вернуть null, если не вышло.
                        figure.Validate();
                    }
                    catch
                    {
                        return null;
                    }
                    
                    return figure;

                }).ToList()
			};

			foreach (var position in cart.Positions)
			{
				//это делаем для мультитреда в синглинстанс. для мультиинстанса нужен Distributed lock,
				//но писать его надо в репозитории. Раз уж у нас корректно сконфигурированный и готовый к использованию клиент Redis'a,
				//предполагаем, что оно там сделано и мультиинстансы не убьют параллельно идущие данные
				// я про вот это https://github.com/kidfashion/redlock-cs. Ожидаю, что в redis.set где-то внутри оно используется.
				lock (_lockobject)
                {
					FiguresStorage.Reserve(position.Type, position.Count);
				}
				
			}

            //к этому месту есть вопросы, реализация работы с данными скрыта сейчас, так что пердполагаем, что везде добавлены transacrion.rollback ну или что-то подходящее, 
            var result = await _orderStorage.Save(order);
            // предполагаем, что сумма должна быть не нулевой после сохранения, если она 0, то возвращаем 500 ошибку. 
            if (result > 0) return new OkObjectResult(result);
			return new StatusCodeResult(500);



		}
	}
}
