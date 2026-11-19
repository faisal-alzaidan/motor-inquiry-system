
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Motor.Inquiry.Application.Common.Exceptions;
using Motor.Inquiry.Application.DTOs;
using Motor.Inquiry.Application.Interfaces;
using Motor.Inquiry.Application.Resources;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Motor.Inquiry.Application.Services
{
    public class MotorInquiryService : IMotorInquiryService
    {
        private readonly IYaqeenClientService _yaqeenClientService;
        private readonly IInsuranceOfferService _insuranceOfferService;
        private readonly IDistributedCache _distributedCache;
        private readonly IStringLocalizer<SharedResource> _localizer;
        private readonly ILogger<MotorInquiryService> _logger;

        public MotorInquiryService(
            IYaqeenClientService yaqeenClientService,
            IInsuranceOfferService insuranceOfferService,
            IDistributedCache distributedCache,
            IStringLocalizer<SharedResource> localizer,
            ILogger<MotorInquiryService> logger)
        {
            _yaqeenClientService = yaqeenClientService;
            _insuranceOfferService = insuranceOfferService;
            _distributedCache = distributedCache;
            _localizer = localizer;
            _logger = logger;
        }

        public async Task<ApiResponse<MotorInquiryResponseDto>> ProcessInquiryAsync(
            MotorInquiryRequestDto request)
        {
            // 1. Generate a unique cache key
            string cacheKey = GenerateCacheKey(request);

            VehicleDetailsDto? vehicleDetails = null;

            // 2. Check Redis Cache FIRST
            var cachedData = await _distributedCache.GetStringAsync(cacheKey);

            if (!string.IsNullOrEmpty(cachedData))
            {
                vehicleDetails =
                    JsonSerializer.Deserialize<VehicleDetailsDto>(cachedData);
            }

            if (vehicleDetails != null)
            {
                _logger.LogInformation(
                    "Redis CACHE HIT. Vehicle retrieved from Redis.");

                // Verify ownership
                if (vehicleDetails.OwnerNationalId != request.NationalId)
                {
                    throw new BadRequestException(
                        _localizer["OwnershipMismatch"]);
                }
            }
            else
            {
                // 3. Cache MISS: Validate citizen through Yaqeen
                _logger.LogInformation(
                    "Redis CACHE MISS. Calling Yaqeen APIs.");

                var isCitizenValid =
                    await _yaqeenClientService.ValidateCitizenAsync(
                        request.NationalId,
                        request.DateOfBirth);

                if (!isCitizenValid)
                {
                    throw new BadRequestException(
                        _localizer["Invalid National ID or Date of Birth."]);
                }

                // 4. Fetch vehicle from Yaqeen
                var yaqeenResponse =
                    await _yaqeenClientService.VerifyVehicleAsync(request);

                if (!yaqeenResponse.Success || yaqeenResponse.Data == null)
                {
                    throw new NotFoundException(
                        _localizer["VehicleNotFound"]);
                }

                vehicleDetails = yaqeenResponse.Data;

                // 5. Verify vehicle ownership
                if (vehicleDetails.OwnerNationalId != request.NationalId)
                {
                    throw new BadRequestException(
                        _localizer["OwnershipMismatch"]);
                }

                // 6. Save vehicle details in Redis for 120 seconds
                var cacheOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow =
                        TimeSpan.FromSeconds(120)
                };

                var serializedData =
                    JsonSerializer.Serialize(vehicleDetails);

                await _distributedCache.SetStringAsync(
                    cacheKey,
                    serializedData,
                    cacheOptions);

                _logger.LogInformation(
                    "Vehicle successfully saved in Redis. TTL: 120 seconds.");
            }

            // 7. Generate insurance offers
            var insuranceOffers =
                _insuranceOfferService.GetOffersForVehicle(vehicleDetails);

            return CreateResponse(
                success: true,
                message: _localizer["VehicleFetchedSuccess"],
                vehicle: vehicleDetails,
                offers: insuranceOffers
            );
        }

        // Generate hashed cache key to avoid exposing National ID in Redis keys
        private static string GenerateCacheKey(
            MotorInquiryRequestDto request)
        {
            string keyData = string.Join("|",
                request.NationalId.Trim(),
                request.DateOfBirth.ToString("yyyyMMdd"),
                request.SequenceNumber?.Trim() ?? "",
                request.PlateLetters?.Trim().ToUpperInvariant() ?? "",
                request.PlateNumber?.Trim() ?? ""
            );

            byte[] hashBytes = SHA256.HashData(
                Encoding.UTF8.GetBytes(keyData));

            string hash = Convert.ToHexString(hashBytes);

            return $"motor_inquiry_{hash}";
        }

        private static ApiResponse<MotorInquiryResponseDto> CreateResponse(
            bool success,
            string message,
            VehicleDetailsDto? vehicle,
            List<InsuranceOfferDto> offers)
        {
            return new ApiResponse<MotorInquiryResponseDto>
            {
                Success = success,
                Message = message,
                Data = new MotorInquiryResponseDto
                {
                    Vehicle = vehicle,
                    InsuranceOffers = offers
                }
            };
        }
    }
}
